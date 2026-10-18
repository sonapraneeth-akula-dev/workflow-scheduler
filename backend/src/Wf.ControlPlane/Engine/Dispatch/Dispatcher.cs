using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Api;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.Retries;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.ControlPlane.Engine.Dispatch;

public enum AgentLossDisposition { AlreadyHandled, WaitingForMachine, Replaced, Failed }

public sealed class Dispatcher(NpgsqlDataSource dataSource, RunStore runs, AgentRegistry agents,
    PlacementEngine placement, TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<bool> AdvanceAsync(Guid runId,
        Func<NpgsqlConnection, RunState, CancellationToken, Task<PlacementDecision?>> advance,
        CancellationToken cancellationToken)
    {
        var lease = runs.CaptureLease();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await runs.FenceAsync(connection, transaction, lease, cancellationToken);
            await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, ReservationManager.AllocationLock);
            var run = await runs.LoadAsync(connection, runId, true, cancellationToken);
            var sequence = run.Snapshot.LastSequence;
            var decision = await advance(connection, run, cancellationToken);
            var telemetry = run.Snapshot.LastSequence == sequence ? null :
                await CommittedRunTelemetry.CaptureAsync(connection, run.Snapshot, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            telemetry?.Emit();
            if (decision?.Agent is { } agent)
            {
                placement.Apply(decision);
                agents.NotifyDispatch(agent.Id);
            }
            return run.Snapshot.LastSequence != sequence;
        }
        finally { gate.Release(); }
    }

    internal async Task<PlacementDecision> PrepareAsync(NpgsqlConnection connection, RunState run,
        PlacementRequest request, DispatchCommand command, ExecutionPolicy policy, short platformRetries,
        short authorRetries, CancellationToken cancellationToken)
    {
        var decision = await ChooseAsync(connection, run, request, cancellationToken);
        if (decision.Agent is not null)
            await SaveDispatchAsync(connection, run, request, command, decision, policy,
                platformRetries, authorRetries, cancellationToken);
        return decision;
    }

    public Task<PlacementDecision> DispatchAsync(RunState run, PlacementRequest request, DispatchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(run, request, command, new ExecutionPolicy(ExecutionPolicy.Default.AttemptTimeout,
            ExecutionPolicy.Default.Retry, request.AutoRetry), cancellationToken);
    }

    public async Task<PlacementDecision> DispatchAsync(RunState run, PlacementRequest request, DispatchCommand command,
        ExecutionPolicy executionPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executionPolicy);
        request = request with { AutoRetry = executionPolicy.AutoRetry };
        var key = $"{request.NodeKey}/{request.TaskKey}";
        if (request.RunId != run.Snapshot.RunId || command.Task is null ||
            command.Task.RunId != request.RunId.ToString("D") || command.Task.NodeKey != request.NodeKey ||
            command.Task.TaskKey != request.TaskKey || command.Task.Attempt != request.Attempt ||
            request.Attempt > short.MaxValue || command.Sha256 != request.PackageSha256 ||
            command.DeadlineAt is null || command.DeadlineAt.ToDateTimeOffset() <= timeProvider.GetUtcNow() ||
            string.IsNullOrWhiteSpace(command.Version) || !Uri.TryCreate(command.PackageUri, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.Scheme == "http" && !uri.IsLoopback)
            throw new ArgumentException("Dispatch does not match its placement request or has invalid package/deadline data.", nameof(command));
        var lease = runs.CaptureLease();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await runs.FenceAsync(connection, transaction, lease, cancellationToken);
            await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, ReservationManager.AllocationLock);
            var durable = await runs.LoadAsync(connection, request.RunId, true, cancellationToken);
            if (durable.Snapshot.LastSequence != run.Snapshot.LastSequence ||
                !durable.Snapshot.Tasks.TryGetValue(key, out var state) || state != ExecutionStatus.Ready ||
                durable.Snapshot.Status != RunStatus.Running)
                throw new InvalidOperationException("Only a committed Ready task in a running run can be dispatched.");
            var decision = await ChooseAsync(connection, durable, request, cancellationToken);
            if (decision.Agent is null) return decision;
            var dispatch = command.Clone();
            var change = await SaveDispatchAsync(connection, durable, request, dispatch, decision, executionPolicy, 0, 0, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            run.Apply(change);
            placement.Apply(decision);
            agents.NotifyDispatch(decision.Agent.Id);
            return decision;
        }
        finally { gate.Release(); }
    }

    public async Task<AgentLossDisposition> ReplaceLostAsync(Guid runId, string taskKey, short attempt,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(taskKey) || attempt <= 0)
            throw new ArgumentException("Agent-loss replacement requires a run, task and positive attempt.");
        var lease = runs.CaptureLease();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await runs.FenceAsync(connection, transaction, lease, cancellationToken);
            await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, ReservationManager.AllocationLock);
            var run = await runs.LoadAsync(connection, runId, true, cancellationToken);
            if (run.Snapshot.Status != RunStatus.Running ||
                !run.Snapshot.Tasks.TryGetValue(taskKey, out var taskStatus) ||
                taskStatus is not (ExecutionStatus.Dispatched or ExecutionStatus.Running))
                return AgentLossDisposition.AlreadyHandled;
            DispatchCommand? original = null;
            PlacementRequest? previous = null;
            ExecutionPolicy? policy = null;
            Guid previousAgent;
            short platformRetries, authorRetries;
            DateTimeOffset runDeadline, nodeDeadline, lostAt;
            await using (var command = new NpgsqlCommand("""
                SELECT a.dispatch_payload,a.placement_request::text,a.execution_policy::text,a.agent_id,
                       a.platform_retries,a.author_retries,r.deadline_at,COALESCE(n.deadline_at,r.deadline_at),a.ended_at
                FROM task_attempts a JOIN runs r ON r.run_day=a.run_day AND r.id=a.run_id
                JOIN node_runs n ON n.run_day=a.run_day AND n.run_id=a.run_id AND n.node_key=split_part(a.task_key,'/',1)
                WHERE a.run_day=uuid_v7_run_day($1) AND a.run_id=$1 AND a.task_key=$2 AND a.attempt=$3
                    AND a.status=7 AND a.failure_category=2 AND a.replacement_processed_at IS NULL
                    AND NOT EXISTS(SELECT 1 FROM task_attempts newer WHERE newer.run_day=a.run_day
                        AND newer.run_id=a.run_id AND newer.task_key=a.task_key AND newer.attempt>a.attempt)
                FOR UPDATE OF a
                """, connection))
            {
                command.Parameters.AddWithValue(runId);
                command.Parameters.AddWithValue(taskKey);
                command.Parameters.AddWithValue(attempt);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return AgentLossDisposition.AlreadyHandled;
                if (!reader.IsDBNull(0))
                {
                    original = DispatchCommand.Parser.ParseFrom(reader.GetFieldValue<byte[]>(0));
                    previous = JsonSerializer.Deserialize(reader.GetString(1), DispatchJson.Default.PlacementRequest);
                    policy = JsonSerializer.Deserialize(reader.GetString(2), DispatchJson.Default.ExecutionPolicy);
                }
                previousAgent = reader.GetGuid(3);
                platformRetries = reader.GetInt16(4);
                authorRetries = reader.GetInt16(5);
                runDeadline = reader.GetFieldValue<DateTimeOffset>(6);
                nodeDeadline = reader.GetFieldValue<DateTimeOffset>(7);
                lostAt = reader.GetFieldValue<DateTimeOffset>(8);
            }
            var now = timeProvider.GetUtcNow();
            if (original is not null && previous is not null &&
                (previous.RunId != runId || $"{previous.NodeKey}/{previous.TaskKey}" != taskKey || previous.Attempt != attempt ||
                 original.Task is null || original.Task.RunId != runId.ToString("D") ||
                 original.Task.NodeKey != previous.NodeKey || original.Task.TaskKey != previous.TaskKey ||
                 original.Task.Attempt != attempt || original.Sha256 != previous.PackageSha256 ||
                 string.IsNullOrWhiteSpace(original.Version) || !Uri.TryCreate(original.PackageUri, UriKind.Absolute, out var packageUri) ||
                 packageUri.Scheme is not ("https" or "http") || packageUri.Scheme == "http" && !packageUri.IsLoopback))
                throw new InvalidDataException("Persisted dispatch recovery inputs do not match their attempt identity.");
            string? failure = original is null || previous is null || policy is null
                ? "Original dispatch recovery inputs are unavailable; automatic re-placement is unsafe."
                : !policy.AutoRetry ? "Automatic re-placement is disabled by NoAutoRetry."
                : platformRetries >= RetryPolicyEvaluator.PlatformRetryBudget ? "The two platform retries are exhausted."
                : attempt == short.MaxValue ? "The attempt identity range is exhausted." : null;
            var placementFailure = run.Snapshot.Placement is { SingleMachine: true, Machine: not null };
            if (placementFailure) failure = "The named single-machine worker was lost.";
            if (failure is null)
            {
                var retry = RetryPolicyEvaluator.Evaluate(policy!, new(FailureCategory.AgentLost, authorRetries,
                    platformRetries, now, runDeadline, nodeDeadline), 0.5);
                if (!retry.Retry || retry.Budget != RetryBudget.Platform) failure = retry.Reason;
            }
            if (failure is not null)
            {
                await RunStore.AppendAsync(connection, run,
                    run.CreateEvent(EventEntity.Task, taskKey,
                        (int)(placementFailure ? await PlacementOutcome() : ExecutionStatus.Failed), now) with
                        { Attempt = attempt, AttemptEndedAt = lostAt, AttemptStatus = ExecutionStatus.Failed,
                            FailureCategory = placementFailure ? FailureCategory.Placement : FailureCategory.AgentLost }, cancellationToken);
                await RunStore.ExecuteAsync(connection, """
                    UPDATE task_attempts SET error=$4,replacement_processed_at=$5
                    WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                    """, cancellationToken, runId, taskKey, attempt, failure, now);
                await transaction.CommitAsync(cancellationToken);
                return AgentLossDisposition.Failed;
            }
            var request = previous! with { Attempt = attempt + 1, RePlacement = true,
                PreviousAgentId = previousAgent, AutoRetry = true };
            var decision = await ChooseAsync(connection, run, request, cancellationToken);
            if (decision.Agent is null)
            {
                if (now < lostAt.AddMinutes(10)) return AgentLossDisposition.WaitingForMachine;
                await RunStore.AppendAsync(connection, run,
                    run.CreateEvent(EventEntity.Task, taskKey, (int)await PlacementOutcome(), now) with
                    {
                        Attempt = attempt, AttemptEndedAt = lostAt, AttemptStatus = ExecutionStatus.Failed,
                        FailureCategory = FailureCategory.Placement
                    }, cancellationToken);
                await RunStore.ExecuteAsync(connection, """
                    UPDATE task_attempts SET replacement_processed_at=$4
                    WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                    """, cancellationToken, runId, taskKey, attempt, now);
                await transaction.CommitAsync(cancellationToken);
                return AgentLossDisposition.Failed;
            }
            var dispatch = original!.Clone();
            dispatch.Task.Attempt = checked((uint)request.Attempt);
            var deadline = now + TimeSpan.FromTicks(Math.Min(policy!.AttemptTimeout.Ticks,
                (DateTimeOffset.MaxValue - now).Ticks));
            deadline = deadline < runDeadline ? deadline : runDeadline;
            deadline = deadline < nodeDeadline ? deadline : nodeDeadline;
            dispatch.DeadlineAt = Timestamp.FromDateTimeOffset(deadline);
            await RunStore.AppendAsync(connection, run,
                run.CreateEvent(EventEntity.Task, taskKey, (int)ExecutionStatus.Retrying, now) with
                    { Attempt = attempt, AttemptEndedAt = lostAt, AttemptStatus = ExecutionStatus.Failed,
                        FailureCategory = FailureCategory.AgentLost }, cancellationToken);
            await SaveDispatchAsync(connection, run, request, dispatch, decision, policy,
                checked((short)(platformRetries + 1)), authorRetries, cancellationToken);
            await RunStore.ExecuteAsync(connection, """
                UPDATE task_attempts SET replacement_processed_at=$4
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                """, cancellationToken, runId, taskKey, attempt, now);
            await transaction.CommitAsync(cancellationToken);
            placement.Apply(decision);
            agents.NotifyDispatch(decision.Agent.Id);
            return AgentLossDisposition.Replaced;

            async Task<ExecutionStatus> PlacementOutcome()
            {
                var nodes = await RunExecutionService.PlacementNodesAsync(connection, run, cancellationToken);
                var key = taskKey.Split('/');
                return nodes.Single(node => node.Key == key[0]).Tasks.Single(task => task.Key == key[1]).Failure == FailurePolicy.Soft
                    ? ExecutionStatus.SoftFailed : ExecutionStatus.Failed;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<PlacementDecision> ChooseAsync(NpgsqlConnection connection, RunState run, PlacementRequest request,
        CancellationToken cancellationToken)
    {
        var prefix = $"{request.RunId:D}/";
        if (run.Snapshot.Metrics!.Tasks.Values.Any(task => !task.Attempts.IsEmpty) &&
            !placement.Snapshot.NodePins.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var committed = new List<(PlacementRequest Request, Guid AgentId)>();
            await using (var command = new NpgsqlCommand("""
                SELECT a.placement_request::text,a.agent_id,a.task_key,a.attempt,e.seq FROM task_attempts a
                LEFT JOIN (
                    SELECT entity_key,attempt,min(seq) AS seq FROM run_events
                    WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND kind=2 AND attempt IS NOT NULL
                    GROUP BY entity_key,attempt
                ) e ON e.entity_key=a.task_key AND e.attempt=a.attempt
                WHERE a.run_day=uuid_v7_run_day($1) AND a.run_id=$1 AND a.placement_request IS NOT NULL
                ORDER BY e.seq
                """, connection))
            {
                command.Parameters.AddWithValue(request.RunId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var recorded = JsonSerializer.Deserialize(reader.GetString(0), DispatchJson.Default.PlacementRequest)
                        ?? throw new InvalidDataException("A committed placement request is null.");
                    if (reader.IsDBNull(4) || recorded.RunId != request.RunId ||
                        $"{recorded.NodeKey}/{recorded.TaskKey}" != reader.GetString(2) ||
                        recorded.Attempt != reader.GetInt16(3))
                        throw new InvalidDataException("A committed placement does not match its durable dispatch identity.");
                    committed.Add((recorded, reader.GetGuid(1)));
                }
            }
            if (committed.Count != run.Snapshot.Metrics.Tasks.Values.Sum(task => task.Attempts.Length))
                throw new InvalidDataException("The committed placement history is incomplete.");
            placement.RestoreRun(request.RunId, committed);
        }
        var capacity = agents.DispatchableAgents().Select(agent => agent.Capacity(timeProvider.GetUtcNow())).ToDictionary(agent => agent.Id);
        var activeAgents = new HashSet<Guid>();
        {
            await using var batch = new NpgsqlBatch(connection);
            var metadata = new NpgsqlBatchCommand("SELECT id,labels,pool,status,last_heartbeat_at FROM agents WHERE id=ANY($1)");
            metadata.Parameters.AddWithValue(capacity.Keys.ToArray());
            batch.BatchCommands.Add(metadata);
            batch.BatchCommands.Add(new NpgsqlBatchCommand("""
                SELECT agent_id,run_id,node_key,epoch,expires_at,release_requested_at IS NULL
                FROM reservations WHERE released_at IS NULL
                """));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("""
                SELECT agent_id,count(*),sum(required_ram_mb),bool_or(run_id=$1)
                FROM task_attempts WHERE status IN (2,3) GROUP BY agent_id
                """));
            batch.BatchCommands[^1].Parameters.AddWithValue(request.RunId);
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                capacity[id] = capacity[id] with
                {
                    Labels = AgentRegistry.ParseLabels(reader.GetFieldValue<string[]>(1)), Pool = reader.GetString(2),
                    Healthy = reader.GetInt16(3) == 0 &&
                        reader.GetFieldValue<DateTimeOffset>(4) > timeProvider.GetUtcNow() - AgentRegistry.HeartbeatTimeout
                };
            }
            var owned = new HashSet<Guid>();
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var runId = reader.GetGuid(1);
                var node = reader.GetString(2);
                if (runId == request.RunId && node == request.NodeKey) owned.Add(id);
                if (capacity.TryGetValue(id, out var agent))
                    capacity[id] = agent with
                    {
                        ReservedForRun = runId, ReservedForNode = node, ReservationEpoch = reader.GetInt64(3),
                        Healthy = agent.Healthy && reader.GetFieldValue<DateTimeOffset>(4) > timeProvider.GetUtcNow() &&
                            reader.GetBoolean(5)
                    };
            }
            if (owned.Count > 0) capacity = capacity.Where(pair => owned.Contains(pair.Key)).ToDictionary();
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0) && reader.GetBoolean(3)) activeAgents.Add(reader.GetGuid(0));
                if (!reader.IsDBNull(0) && capacity.TryGetValue(reader.GetGuid(0), out var agent))
                    capacity[agent.Id] = agent with
                    {
                        SlotsUsed = Math.Min(agent.MaxTasks, Math.Max(agent.SlotsUsed, checked((int)reader.GetInt64(1)))),
                        AvailableRamMb = (long)Math.Max(0m, agent.AvailableRamMb - reader.GetFieldValue<decimal>(2))
                    };
            }
        }
        if (run.Snapshot.Placement is { SingleMachine: true } single)
        {
            var nodes = await RunExecutionService.PlacementNodesAsync(connection, run, cancellationToken);
            var pin = run.Snapshot.PlacementAgentId;
            if (request.RePlacement && pin == request.PreviousAgentId && activeAgents.Count == 0) pin = null;
            capacity = capacity.Where(pair => (pin is null || pair.Key == pin) &&
                PlacementOverrideValidator.Matches(pair.Value, single, nodes)).ToDictionary();
            // The durable run pin replaces per-process affinity state, including after leader recovery.
            request = request with { Policy = new(Affinity: new(AffinityKind.None)) };
        }
        return placement.Propose(request, capacity.Values.ToArray());
    }

    private async Task<RunEvent> SaveDispatchAsync(NpgsqlConnection connection, RunState run, PlacementRequest request,
        DispatchCommand dispatch, PlacementDecision decision, ExecutionPolicy policy, short platformRetries, short authorRetries,
        CancellationToken cancellationToken)
    {
        var agentId = decision.Agent?.Id ?? throw new InvalidOperationException("Dispatch requires a selected agent.");
        await using (var epoch = new NpgsqlCommand("SELECT reservation_epoch FROM agents WHERE id=$1", connection))
        {
            epoch.Parameters.AddWithValue(agentId);
            dispatch.ReservationEpoch = await epoch.ExecuteScalarAsync(cancellationToken) is long current
                ? checked((ulong)current) : throw new InvalidDataException("Dispatch machine has no authoritative reservation epoch.");
        }
        var key = $"{request.NodeKey}/{request.TaskKey}";
        var change = run.CreateEvent(EventEntity.Task, key, (int)ExecutionStatus.Dispatched, timeProvider.GetUtcNow()) with
        {
            Attempt = request.Attempt, AgentId = agentId, SelectionReason = decision.Reason.ToString(),
            PlannedAgentId = run.Snapshot.Placement?.SingleMachine == true
                ? run.Snapshot.PlacementAgentId : decision.PlannedAgentId
        };
        await RunStore.AppendAsync(connection, run, change, cancellationToken);
        var metric = run.Snapshot.Metrics!.Tasks[key].Attempts[^1];
        await RunStore.ExecuteAsync(connection, """
            WITH attempt AS (
                INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,queued_at,deadline_at,required_ram_mb,
                    dispatch_payload,placement_request,execution_policy,platform_retries,author_retries,queue_wait_ms,reservation_epoch)
                VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,2,$5,$6,$7,$8,$9::jsonb,$10::jsonb,$11,$12,$13,$14)
                RETURNING run_day,run_id,task_key,attempt,agent_id
            )
            INSERT INTO dispatch_outbox(run_day,run_id,task_key,attempt,agent_id,payload)
            SELECT run_day,run_id,task_key,attempt,agent_id,$8 FROM attempt
            """, cancellationToken, request.RunId, key, (short)request.Attempt, agentId,
            metric.QueuedAt, dispatch.DeadlineAt.ToDateTimeOffset(), request.RequiredRamMb, dispatch.ToByteArray(),
            JsonSerializer.Serialize(request, DispatchJson.Default.PlacementRequest),
            JsonSerializer.Serialize(policy, DispatchJson.Default.ExecutionPolicy), platformRetries, authorRetries,
            metric.QueueWaitMs, checked((long)dispatch.ReservationEpoch));
        return change;
    }

    public void Dispose() => gate.Dispose();
}

[JsonSerializable(typeof(PlacementRequest))]
[JsonSerializable(typeof(ExecutionPolicy))]
internal sealed partial class DispatchJson : JsonSerializerContext;
