using System.Collections.Immutable;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Engine.Core.Abort;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.Retries;
using Wf.Engine.Core.Scheduling;
using Wf.Engine.Core.Stages;
using Wf.Engine.Core.StateMachine;
using Wf.Engine.Core.Transitions;
using Wf.Validation;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.ControlPlane.Engine.Dispatch;

public sealed record RunExecutionOptions(Uri ApiEndpoint);

public sealed class RunExecutionService(NpgsqlDataSource source, RunRecovery recovery, Dispatcher dispatcher,
    ReservationManager reservations, TimeProvider time, RunExecutionOptions options,
    [FromKeyedServices(PackageDownloadEndpoints.Grants)] BlobUploadGrants downloads,
    ILogger<RunExecutionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (recovery.IsReady) await TickAsync(stoppingToken);
                }
                catch (NotLeaderException) { logger.LogInformation("Run execution paused after losing leadership."); }
                catch (Exception error) when (error is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
                { logger.LogError(error, "Run execution paused until durable storage is available."); }
                await Task.Delay(TimeSpan.FromMilliseconds(25), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task TickAsync(CancellationToken token)
    {
        var ids = new List<Guid>();
        await using (var query = source.CreateCommand("SELECT id FROM runs WHERE status IN (0,1,2) ORDER BY created_at,id"))
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) ids.Add(reader.GetGuid(0));
        foreach (var id in ids)
            for (var step = 0; step < 256 && await dispatcher.AdvanceAsync(id, AdvanceAsync, token); step++) { }
    }

    private async Task<PlacementDecision?> AdvanceAsync(NpgsqlConnection connection, RunState run, CancellationToken token)
    {
        if (TransitionTable.IsTerminal(run.Snapshot.Status)) return null;
        var definition = await ReadAsync(connection, run, token);
        var now = time.GetUtcNow();
        var nodes = definition.Nodes;
        var timedOut = now >= definition.Deadline;
        if (run.Snapshot.Status == RunStatus.Queued)
            await Emit(EventEntity.Run, null, (int)RunStatus.Running);
        if (run.Snapshot.Status == RunStatus.Aborting)
        {
            var abort = AbortHandler.Plan(nodes, run.Snapshot);
            foreach (var change in abort.Tasks) await Emit(EventEntity.Task, change.Task, (int)change.Status);
            foreach (var change in abort.Nodes) await Emit(EventEntity.Node, change.Node, (int)change.Status);
            foreach (var key in abort.CancelRunning) await Cancel(key, FailureCategory.Aborted);
            if (abort.Status is { } status)
            {
                await Emit(EventEntity.Run, null, (int)status);
                return null;
            }
        }
        var deadlines = new Dictionary<string, DateTimeOffset>();
        await using (var query = new NpgsqlCommand("""
            SELECT node_key,deadline_at FROM node_runs
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND deadline_at IS NOT NULL
            """, connection))
        {
            query.Parameters.AddWithValue(run.Snapshot.RunId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) deadlines.Add(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
        }
        foreach (var node in nodes.Where(node => run.Snapshot.Nodes[node.Key] == ExecutionStatus.Running))
        {
            var expired = deadlines.GetValueOrDefault(node.Key, DateTimeOffset.MaxValue) <= now ||
                timedOut && !Cleanup(node);
            var failed = node.Tasks.Any(task => task.Failure == FailurePolicy.Hard &&
                run.Snapshot.Tasks[Key(node, task)] is ExecutionStatus.Failed or ExecutionStatus.TimedOut);
            foreach (var task in node.Tasks)
            {
                var key = Key(node, task);
                if (TransitionTable.IsTerminal(run.Snapshot.Tasks[key])) continue;
                if (expired || failed)
                {
                    if (run.Snapshot.Tasks[key] is ExecutionStatus.Dispatched or ExecutionStatus.Running)
                        await Cancel(key, expired ? FailureCategory.Timeout : FailureCategory.Aborted);
                    else
                        await Emit(EventEntity.Task, key, (int)(expired ? ExecutionStatus.Skipped : ExecutionStatus.Cancelled));
                }
            }
            var local = node.Tasks.ToDictionary(task => task.Key, task => run.Snapshot.Tasks[Key(node, task)]);
            if (FailurePropagation.ResolveNode(node, local) is { } outcome)
                await Emit(EventEntity.Node, node.Key, (int)(expired
                    ? node.Failure == FailurePolicy.Soft ? ExecutionStatus.SoftFailed : ExecutionStatus.TimedOut
                    : outcome), expired ? FailureCategory.Timeout : null);
        }
        var stage = StagePlanner.Plan(nodes, run.Snapshot.Nodes, run.Snapshot.Status, timedOut);
        foreach (var change in stage.Changes) await ChangeNode(change);
        foreach (var change in ReadinessEvaluator.Evaluate(nodes, run.Snapshot.Nodes, stage.EligibleNodes))
            await ChangeNode(change);
        if (stage.Complete)
        {
            if (run.Snapshot.Tasks.Values.Any(status => !TransitionTable.IsTerminal(status)))
                throw new InvalidDataException("A durable workflow cannot finish before all attempts drain.");
            var status = run.Snapshot.Status == RunStatus.Aborting ? RunStatus.Aborted : timedOut ? RunStatus.TimedOut :
                run.Snapshot.Nodes.Values.Any(status => status is ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Cancelled)
                    ? RunStatus.Failed : RunStatus.Succeeded;
            await Emit(EventEntity.Run, null, (int)status, status == RunStatus.TimedOut ? FailureCategory.Timeout : null);
            return null;
        }
        foreach (var node in nodes)
        {
            if (run.Snapshot.Nodes[node.Key] == ExecutionStatus.Ready)
            {
                if (node.Placement?.Selector is { Count: { } count } selector)
                {
                    var result = await reservations.RequestLockedAsync(connection, run.Snapshot.RunId, node.Key,
                        count, selector, TimeSpan.FromMinutes(1), node.WhenUnavailable, token);
                    if (result.Disposition != ReservationDisposition.Reserved) return null;
                }
                await Emit(EventEntity.Node, node.Key, (int)ExecutionStatus.Dispatched);
                await Emit(EventEntity.Node, node.Key, (int)ExecutionStatus.Running);
                if (node.Timeout is { } timeout)
                {
                    var deadline = Add(now, timeout);
                    deadlines[node.Key] = deadline;
                    await RunStore.ExecuteAsync(connection, """
                        UPDATE node_runs SET deadline_at=$3 WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                        """, token, run.Snapshot.RunId, node.Key, deadline);
                }
            }
            if (run.Snapshot.Nodes[node.Key] != ExecutionStatus.Running) continue;
            var limit = node.Mode == NodeExecutionMode.Series ? 1 : node.MaxParallel ?? int.MaxValue;
            var active = node.Tasks.Count(task => run.Snapshot.Tasks[Key(node, task)] is ExecutionStatus.Dispatched or ExecutionStatus.Running);
            foreach (var task in node.Tasks)
            {
                if (active >= limit) break;
                var key = Key(node, task);
                var status = run.Snapshot.Tasks[key];
                if (TransitionTable.IsTerminal(status)) continue;
                if (status is ExecutionStatus.Dispatched or ExecutionStatus.Running)
                {
                    if (node.Mode == NodeExecutionMode.Series) break;
                    continue;
                }
                if (status == ExecutionStatus.Pending) await Emit(EventEntity.Task, key, (int)ExecutionStatus.Ready);
                var attempt = 1;
                short authorRetries = 0, platformRetries = 0;
                if (status == ExecutionStatus.Retrying)
                {
                    var previous = await LastAttemptAsync(connection, run.Snapshot.RunId, key, token);
                    if (previous.RetryAt is null)
                        throw new InvalidDataException("A retrying task has no durable retry deadline.");
                    if (previous.RetryAt > now)
                    {
                        if (node.Mode == NodeExecutionMode.Series) break;
                        continue;
                    }
                    attempt = checked(previous.Attempt + 1);
                    authorRetries = previous.AuthorRetries;
                    platformRetries = previous.PlatformRetries;
                    if (previous.Category is FailureCategory.PlatformError or FailureCategory.AgentLost &&
                        task.Policy.AutoRetry && platformRetries < RetryPolicyEvaluator.PlatformRetryBudget)
                        platformRetries++;
                    else authorRetries++;
                }
                var deadline = Add(now, task.Policy.AttemptTimeout);
                if (deadlines.TryGetValue(node.Key, out var nodeDeadline) && nodeDeadline < deadline) deadline = nodeDeadline;
                if (!Cleanup(node) && definition.Deadline < deadline) deadline = definition.Deadline;
                if (deadline <= now) return null;
                var outputs = await OutputsAsync(connection, run.Snapshot.RunId, token);
                var inputs = node.Inputs.GroupBy(input => input.Node, StringComparer.Ordinal).ToDictionary(
                    group => group.Key, group => outputs.TryGetValue(group.Key, out var sourceOutputs)
                        ? group.Where(input => sourceOutputs.ContainsKey(input.Key))
                            .ToDictionary(input => input.Key, input => sourceOutputs[input.Key], StringComparer.Ordinal)
                        : new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
                var dispatch = new DispatchCommand
                {
                    Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = node.Key, TaskKey = task.Key,
                        Attempt = checked((uint)attempt) },
                    Version = definition.Manifest.Version, Sha256 = definition.Package.Sha256,
                    PackageUri = new Uri(options.ApiEndpoint, "/api/v1/packages/" +
                        downloads.Create(definition.Package, definition.Package.SizeBytes, TimeSpan.FromMinutes(10))).AbsoluteUri,
                    DeadlineAt = Timestamp.FromDateTimeOffset(deadline),
                    Limits = task.Policy.Limits is { } limits
                        ? new() { CpuCores = limits.CpuCores, RamMb = limits.RamMb } : null,
                    VariablesJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(run.Snapshot.Variables,
                        RunStoreJson.Default.ImmutableDictionaryStringJsonElement)),
                    InputsJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(inputs)),
                    NodeOutputsJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                        outputs.GetValueOrDefault(node.Key, new Dictionary<string, JsonElement>()))),
                    AbortJson = run.Snapshot.Abort is { } abort
                        ? ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(abort, AbortRecordJsonContext.Default.AbortRecord))
                        : ByteString.Empty
                };
                var decision = await dispatcher.PrepareAsync(connection, run,
                    new(run.Snapshot.RunId, node.Key, task.Key, attempt, definition.Package.Sha256, task.Placement,
                        RequiredRamMb: checked((long)(task.Policy.Limits?.RamMb ?? 0))),
                    dispatch, task.Policy, platformRetries, authorRetries, token);
                var queuedAt = run.Snapshot.Metrics!.Tasks[key].QueuedAt ??
                    throw new InvalidDataException("A dispatchable task has no durable queue timestamp.");
                if (decision.Agent is null && queuedAt <= now.AddMinutes(-10))
                {
                    await Emit(EventEntity.Task, key,
                        (int)(task.Failure == FailurePolicy.Soft ? ExecutionStatus.SoftFailed : ExecutionStatus.Failed),
                        FailureCategory.Placement);
                    return null;
                }
                if (decision.Agent is not null) return decision;
                if (run.Snapshot.Tasks[key] == ExecutionStatus.Ready &&
                    !run.Snapshot.TaskWaitReasons.ContainsKey(key))
                    await RunStore.AppendAsync(connection, run,
                        new RunEvent(run.Snapshot.RunId, checked(run.Snapshot.LastSequence + 1), now,
                            EventEntity.Task, key, (int)ExecutionStatus.Ready) { WaitingReason = "WaitingForMachine" }, token);
                if (node.Mode == NodeExecutionMode.Series) break;
            }
        }
        return null;

        async Task Emit(EventEntity entity, string? key, int state, FailureCategory? category = null) =>
            await RunStore.AppendAsync(connection, run,
                run.CreateEvent(entity, key, state, now) with { FailureCategory = category }, token);

        async Task ChangeNode(NodeStateChange change)
        {
            if (change.Status == ExecutionStatus.Skipped)
                foreach (var task in nodes.Single(node => node.Key == change.Node).Tasks)
                {
                    var key = change.Node + "/" + task.Key;
                    if (!TransitionTable.IsTerminal(run.Snapshot.Tasks[key]))
                        await Emit(EventEntity.Task, key, (int)ExecutionStatus.Skipped);
                }
            await Emit(EventEntity.Node, change.Node, (int)change.Status);
        }

        Task Cancel(string key, FailureCategory category) => RunStore.ExecuteAsync(connection, """
            UPDATE task_attempts SET cancel_requested_at=$3,kill_at=$3+interval '30 seconds',timer_phase=1,failure_category=$4
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND status IN (2,3)
                AND cancel_requested_at IS NULL
            """, token, run.Snapshot.RunId, key, now, (short)category);
    }

    internal static async Task<Completion> CompleteAsync(NpgsqlConnection connection, RunState run,
        TaskCompleted completed, ExecutionStatus outcome, FailureCategory? category, DateTimeOffset now, CancellationToken token)
    {
        var definition = await ReadAsync(connection, run, token);
        var node = definition.Nodes.Single(node => node.Key == completed.Task.NodeKey);
        var task = node.Tasks.Single(task => task.Key == completed.Task.TaskKey);
        var previous = await LastAttemptAsync(connection, run.Snapshot.RunId, Key(node, task), token);
        var cancelled = previous.Cancelled || run.Snapshot.Status == RunStatus.Aborting &&
            run.Snapshot.AbortTargets.Contains(Key(node, task));
        if (cancelled)
        {
            category = previous.Category == FailureCategory.Timeout ? FailureCategory.Timeout : FailureCategory.Aborted;
            outcome = category == FailureCategory.Timeout ? ExecutionStatus.TimedOut : ExecutionStatus.Cancelled;
        }
        if (outcome == ExecutionStatus.Succeeded) return new(outcome, outcome, null, null);
        if (category is { } failure && !cancelled && completed.Task.Attempt < short.MaxValue)
        {
            var retry = RetryPolicyEvaluator.Evaluate(task.Policy,
                new(failure, previous.AuthorRetries, previous.PlatformRetries, now,
                    Cleanup(node) && (now >= definition.Deadline || run.Snapshot.Status == RunStatus.Aborting)
                        ? DateTimeOffset.MaxValue : definition.Deadline,
                    previous.NodeDeadline, Retryable: completed.Error?.NonRetryable != true),
                Random.Shared.NextDouble());
            if (retry.Retry) return new(ExecutionStatus.Retrying, outcome, retry.RetryAt, category);
        }
        if (outcome is ExecutionStatus.Failed or ExecutionStatus.TimedOut && task.Failure == FailurePolicy.Soft)
            outcome = ExecutionStatus.SoftFailed;
        return new(outcome, outcome, null, category);
    }

    internal static async Task<IReadOnlyList<Cancel>> PendingCancelsAsync(NpgsqlConnection connection,
        Guid agentId, Guid sessionId, CancellationToken token)
    {
        var result = new List<Cancel>();
        await using var query = new NpgsqlCommand("""
            SELECT t.run_id,t.task_key,t.attempt,t.failure_category FROM task_attempts t JOIN agents a ON a.id=t.agent_id
            WHERE t.agent_id=$1 AND a.session_id=$2 AND t.status IN (2,3) AND t.cancel_requested_at IS NOT NULL
                AND t.reservation_epoch=a.reservation_epoch ORDER BY t.run_id,t.task_key,t.attempt
            """, connection);
        query.Parameters.AddWithValue(agentId);
        query.Parameters.AddWithValue(sessionId);
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(1).Split('/');
            result.Add(new()
            {
                Task = new() { RunId = reader.GetGuid(0).ToString("D"), NodeKey = key[0], TaskKey = key[1],
                    Attempt = checked((uint)reader.GetInt16(2)) },
                GraceMs = 30_000, Reason = reader.GetInt16(3) == (short)FailureCategory.Timeout ? "Execution deadline expired." : "Execution cancelled."
            });
        }
        return result;
    }

    private static async Task<Definition> ReadAsync(NpgsqlConnection connection, RunState run, CancellationToken token)
    {
        await using var query = new NpgsqlCommand("""
            SELECT r.workflow_id,r.deadline_at,v.semver,v.manifest::text,v.package_sha256,v.package_size
            FROM runs r JOIN workflow_versions v ON v.id=r.version_id AND v.workflow_id=r.workflow_id
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1
            """, connection);
        query.Parameters.AddWithValue(run.Snapshot.RunId);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("A run has no pinned workflow package.");
        var manifest = JsonSerializer.Deserialize(reader.GetString(3), WorkflowManifestJsonContext.Default.WorkflowManifest)
            ?? throw new InvalidDataException("A run has no workflow manifest.");
        if (manifest.Name != reader.GetString(0) || manifest.Version != reader.GetString(2) ||
            !manifest.Nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal).SetEquals(run.Snapshot.Nodes.Keys) ||
            !manifest.Nodes.SelectMany(node => node.Tasks.Select(task => Key(node, task)))
                .ToHashSet(StringComparer.Ordinal).SetEquals(run.Snapshot.Tasks.Keys))
            throw new InvalidDataException("The immutable workflow definition does not match its run.");
        var variables = VariableBinder.Bind(manifest.Variables, run.Snapshot.Variables);
        return new(manifest, PlacementResolver.Resolve(SelectorValidator.EffectiveNodes(manifest), variables),
            new(BlobKind.Packages, Convert.ToHexString(reader.GetFieldValue<byte[]>(4)), reader.GetInt64(5)),
            reader.GetFieldValue<DateTimeOffset>(1));
    }

    internal static async Task<ImmutableArray<NodeDefinition>> PlacementNodesAsync(NpgsqlConnection connection,
        RunState run, CancellationToken token) => (await ReadAsync(connection, run, token)).Nodes;

    private static async Task<StoredAttempt> LastAttemptAsync(NpgsqlConnection connection, Guid runId, string key, CancellationToken token)
    {
        await using var query = new NpgsqlCommand("""
            SELECT t.attempt,t.author_retries,t.platform_retries,t.retry_at,t.failure_category,
                   t.cancel_requested_at IS NOT NULL,n.deadline_at
            FROM task_attempts t JOIN node_runs n ON n.run_day=t.run_day AND n.run_id=t.run_id
                AND n.node_key=split_part(t.task_key,'/',1)
            WHERE t.run_day=uuid_v7_run_day($1) AND t.run_id=$1 AND t.task_key=$2 ORDER BY t.attempt DESC LIMIT 1
            """, connection);
        query.Parameters.AddWithValue(runId);
        query.Parameters.AddWithValue(key);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("An executing task has no durable attempt.");
        return new(reader.GetInt16(0), reader.GetInt16(1), reader.GetInt16(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : (FailureCategory)reader.GetInt16(4), reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static async Task<Dictionary<string, Dictionary<string, JsonElement>>> OutputsAsync(
        NpgsqlConnection connection, Guid runId, CancellationToken token)
    {
        var result = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        await using var query = new NpgsqlCommand("""
            SELECT node_key,outputs::text FROM node_runs WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND outputs IS NOT NULL
            """, connection);
        query.Parameters.AddWithValue(runId);
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            result.Add(reader.GetString(0), document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal));
        }
        return result;
    }

    private static bool Cleanup(NodeDefinition node) => LifecycleStages.After.Contains(node.Key) ||
        node.Dependencies.Any(dependency => dependency.When == DependencyCondition.Always);
    private static string Key(NodeDefinition node, TaskDefinition task) => node.Key + "/" + task.Key;
    private static DateTimeOffset Add(DateTimeOffset at, TimeSpan duration) =>
        at + TimeSpan.FromTicks(Math.Min(duration.Ticks, (DateTimeOffset.MaxValue - at).Ticks));
    private sealed record Definition(WorkflowManifest Manifest, ImmutableArray<NodeDefinition> Nodes,
        BlobContent Package, DateTimeOffset Deadline);
    private sealed record StoredAttempt(short Attempt, short AuthorRetries, short PlatformRetries, DateTimeOffset? RetryAt,
        FailureCategory? Category, bool Cancelled, DateTimeOffset? NodeDeadline);
    internal sealed record Completion(ExecutionStatus Status, ExecutionStatus AttemptStatus,
        DateTimeOffset? RetryAt, FailureCategory? Category);
}
