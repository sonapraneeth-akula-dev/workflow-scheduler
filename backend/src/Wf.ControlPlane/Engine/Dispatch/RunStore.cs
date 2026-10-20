using System.Text;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Definition;
using Wf.Contracts.Runs;
using Wf.Contracts.Outputs;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Maintenance;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core;
using Wf.Engine.Core.StateMachine;
using Wf.Engine.Core.Results;
using Wf.Engine.Core.Metrics;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.ControlPlane.Engine.Dispatch;

public sealed class RunStore(NpgsqlDataSource dataSource, ILogger<RunStore> logger, LeaseManager? leadership = null,
    TimeProvider? timeProvider = null, RunExecutionOptions? execution = null)
{
    internal DateTimeOffset UtcNow => (timeProvider ?? TimeProvider.System).GetUtcNow();
    internal LeaseToken? CaptureLease() => leadership?.RequireLease();

    internal Task FenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LeaseToken? lease,
        CancellationToken cancellationToken) => leadership is null ? Task.CompletedTask :
        leadership.FenceAsync(connection, transaction, lease ?? throw new NotLeaderException(leadership.LeaderHint),
            cancellationToken);

    public async Task CreateAsync(RunState run, string workflowId, Guid versionId, DateTimeOffset deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        if (run.Snapshot.LastSequence != 0 || versionId == Guid.Empty || deadline <= DateTimeOffset.UtcNow)
            throw new ArgumentException("A new run requires an initial snapshot, version and future deadline.");
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        await CreateAsync(connection, run, workflowId, versionId, deadline, DateTimeOffset.UtcNow, "engine", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task CreateAsync(NpgsqlConnection connection, RunState run, string workflowId, Guid versionId,
        DateTimeOffset deadline, DateTimeOffset createdAt, string createdBy, CancellationToken cancellationToken,
        Guid? seriesId = null, RunTrigger trigger = RunTrigger.Manual, Guid? parentRunId = null, string? triggeredByName = null)
    {
        run = RunState.Restore(run.Snapshot with
            { Metrics = RunMetricsRecorder.Initialize(run.Snapshot, createdAt) }, [], _ => { });
        await ExecuteAsync(connection, "SELECT ensure_run_partitions(uuid_v7_run_day($1))", cancellationToken, run.Snapshot.RunId);
        await ExecuteAsync(connection, """
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,created_at,deadline_at,parent_run_id,placement,triggered_by_name)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$9,$10,$8,$6::jsonb,$7::jsonb,0,$4,$5,$11,$12::jsonb,$13)
            """, cancellationToken, run.Snapshot.RunId, workflowId, versionId, createdAt, deadline,
            JsonSerializer.Serialize(run.Snapshot.Variables, RunStoreJson.Default.ImmutableDictionaryStringJsonElement),
            JsonSerializer.Serialize(run.Snapshot.Metadata, RunStoreJson.Default.WorkflowMetadata), createdBy,
            (object?)seriesId ?? DBNull.Value, (short)trigger, (object?)parentRunId ?? DBNull.Value,
            run.Snapshot.Placement is { } placement
                ? JsonSerializer.Serialize(placement, RunJsonContext.Default.RunPlacement) : DBNull.Value,
            (object?)triggeredByName ?? DBNull.Value);
        await WriteSnapshotAsync(connection, run, cancellationToken);
        foreach (var node in run.Snapshot.Nodes)
            await ExecuteAsync(connection, """
                INSERT INTO node_runs(run_day,run_id,node_key,status) VALUES(uuid_v7_run_day($1),$1,$2,$3)
                """, cancellationToken, run.Snapshot.RunId, node.Key, (short)node.Value);
    }

    public async Task<RunState> LoadAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LoadAsync(connection, runId, false, cancellationToken);
    }

    internal Task<RunState> LoadAsync(NpgsqlConnection connection, Guid runId, bool forUpdate,
        CancellationToken cancellationToken) =>
        LoadAsync(connection, runId, forUpdate, message => logger.LogWarning("{RunStateError}", message), cancellationToken);

    internal static async Task<RunState> LoadAsync(NpgsqlConnection connection, Guid runId, bool forUpdate,
        Action<string> log, CancellationToken cancellationToken)
    {
        byte[] payload;
        long lastSequence;
        await using (var batch = new NpgsqlBatch(connection))
        {
            if (forUpdate)
            {
                // Read a fresh statement snapshot after any concurrent lock holder commits.
                var locked = new NpgsqlBatchCommand("""
                    SELECT 1 FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1 FOR UPDATE
                    """);
                locked.Parameters.AddWithValue(runId);
                batch.BatchCommands.Add(locked);
            }
            var snapshotQuery = new NpgsqlBatchCommand("""
                SELECT s.payload,r.last_seq FROM runs r JOIN run_snapshots s
                  ON s.run_day=r.run_day AND s.run_id=r.id AND s.seq=r.snapshot_seq
                WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1
                """);
            snapshotQuery.Parameters.AddWithValue(runId);
            batch.BatchCommands.Add(snapshotQuery);
            await using var snapshots = await batch.ExecuteReaderAsync(cancellationToken);
            if (forUpdate)
            {
                if (!await snapshots.ReadAsync(cancellationToken))
                    throw new KeyNotFoundException($"Run '{runId}' does not exist.");
                await snapshots.NextResultAsync(cancellationToken);
            }
            if (!await snapshots.ReadAsync(cancellationToken))
                throw new KeyNotFoundException($"Run '{runId}' does not exist.");
            payload = snapshots.GetFieldValue<byte[]>(0);
            lastSequence = snapshots.GetInt64(1);
        }
        var snapshot = JsonSerializer.Deserialize(payload, RunStoreJson.Default.RunSnapshot)
            ?? throw new InvalidDataException("Persisted run snapshot is null.");
        if (snapshot.Metrics is not null && snapshot.LastSequence == lastSequence)
            return RunState.Restore(snapshot, [], log);
        if (snapshot.Metrics is null)
            snapshot = await LoadMetricsBaselineAsync(connection, runId, cancellationToken);
        await using var tail = new NpgsqlCommand("""
            SELECT payload FROM run_events WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND seq>$2 ORDER BY seq
            """, connection);
        tail.Parameters.AddWithValue(runId);
        tail.Parameters.AddWithValue(snapshot.LastSequence);
        var events = new List<RunEvent>();
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(0), RunStoreJson.Default.RunEvent)
                ?? throw new InvalidDataException("Persisted run event is null."));
        return RunState.Restore(snapshot, events, log);
    }

    internal async Task<Dictionary<Guid, RunState>> LoadManyAsync(NpgsqlConnection connection, Guid[] ids,
        CancellationToken cancellationToken)
    {
        // Lock first, then join at a fresh READ COMMITTED snapshot; a concurrent snapshot_seq update
        // must not make PostgreSQL's FOR UPDATE join recheck drop an otherwise valid run.
        await using (var locked = new NpgsqlCommand("""
            SELECT id FROM runs WHERE run_day=ANY(SELECT uuid_v7_run_day(id) FROM unnest($1::uuid[]) id)
                AND id=ANY($1) ORDER BY id FOR UPDATE
            """, connection))
        {
            locked.Parameters.AddWithValue(ids);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken)) count++;
            if (count != ids.Length) throw new InvalidDataException("A recovering run no longer exists.");
        }
        var snapshots = new Dictionary<Guid, RunSnapshot>();
        await using (var command = new NpgsqlCommand("""
            SELECT r.id,s.payload FROM runs r JOIN run_snapshots s
              ON s.run_day=r.run_day AND s.run_id=r.id AND s.seq=r.snapshot_seq
            WHERE r.run_day=ANY(SELECT uuid_v7_run_day(id) FROM unnest($1::uuid[]) id)
                AND r.id=ANY($1)
            """, connection))
        {
            command.Parameters.AddWithValue(ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                snapshots.Add(reader.GetGuid(0),
                    JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(1), RunStoreJson.Default.RunSnapshot)
                        ?? throw new InvalidDataException("Persisted run snapshot is null."));
        }
        if (snapshots.Count != ids.Length) throw new InvalidDataException("A recovering run has no persisted snapshot.");
        var legacy = snapshots.Where(p => p.Value.Metrics is null).Select(p => p.Key).ToArray();
        foreach (var id in legacy)
            snapshots[id] = await LoadMetricsBaselineAsync(connection, id, cancellationToken);
        var events = new Dictionary<Guid, List<RunEvent>>();
        await using (var command = new NpgsqlCommand("""
            SELECT e.run_id,e.payload FROM run_events e JOIN runs r ON r.run_day=e.run_day AND r.id=e.run_id
            WHERE e.run_day=ANY(SELECT uuid_v7_run_day(id) FROM unnest($1::uuid[]) id)
                AND e.run_id=ANY($1) AND e.seq>CASE WHEN e.run_id=ANY($2) THEN 0 ELSE r.snapshot_seq END ORDER BY e.run_id,e.seq
            """, connection))
        {
            command.Parameters.AddWithValue(ids);
            command.Parameters.AddWithValue(legacy);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!events.TryGetValue(id, out var tail)) events.Add(id, tail = []);
                tail.Add(JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(1), RunStoreJson.Default.RunEvent)
                    ?? throw new InvalidDataException("Persisted run event is null."));
            }
        }
        return snapshots.ToDictionary(pair => pair.Key, pair => RunState.Restore(pair.Value,
            events.TryGetValue(pair.Key, out var tail) ? tail : [],
            message => logger.LogWarning("{RunStateError}", message)));
    }

    private static async Task<RunSnapshot> LoadMetricsBaselineAsync(NpgsqlConnection connection, Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT s.payload,r.created_at FROM runs r JOIN run_snapshots s
                ON s.run_day=r.run_day AND s.run_id=r.id AND s.seq=0
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1
            """, connection);
        command.Parameters.AddWithValue(runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("A legacy run has no initial snapshot for metrics replay.");
        var snapshot = JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(0), RunStoreJson.Default.RunSnapshot)
            ?? throw new InvalidDataException("Persisted initial run snapshot is null.");
        return snapshot with { Metrics = RunMetricsRecorder.Initialize(snapshot, reader.GetFieldValue<DateTimeOffset>(1)) };
    }

    public Task AppendAsync(RunState run, EventEntity entity, string? key, int state, DateTimeOffset at,
        CancellationToken cancellationToken = default) =>
        AppendAsync(run, durable => durable.CreateEvent(entity, key, state, at), cancellationToken);

    public Task AppendAsync(RunState run, RunEvent change, CancellationToken cancellationToken = default) =>
        AppendAsync(run, _ => change, cancellationToken);

    private async Task AppendAsync(RunState run, Func<RunState, RunEvent> create,
        CancellationToken cancellationToken)
    {
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        var durable = await LoadAsync(connection, run.Snapshot.RunId, true, cancellationToken);
        if (durable.Snapshot.LastSequence != run.Snapshot.LastSequence)
            throw new InvalidOperationException("Run changed since it was loaded.");
        var change = create(durable);
        await AppendAsync(connection, durable, change, cancellationToken);
        var telemetry = await CommittedRunTelemetry.CaptureAsync(connection, durable.Snapshot, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        run.Apply(change);
        telemetry?.Emit();
    }

    internal static async Task AppendAsync(NpgsqlConnection connection, RunState run, RunEvent change,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            INSERT INTO run_events(run_day,run_id,seq,ts,kind,entity_key,payload,attempt)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,$5,$6,$7)
            """, cancellationToken, change.RunId, change.Sequence, change.At, (short)change.Entity,
            (object?)change.Key ?? DBNull.Value, JsonSerializer.SerializeToUtf8Bytes(change, RunStoreJson.Default.RunEvent),
            (object?)(short?)change.Attempt ?? DBNull.Value);
        run.Apply(change);
        await WriteSnapshotAsync(connection, run, cancellationToken);
        if (change.Entity == EventEntity.Node)
        {
            var metric = run.Snapshot.Metrics!.Nodes[change.Key!];
            await ExecuteAsync(connection, """
                UPDATE node_runs SET status=$3,started_at=$4,ended_at=$5
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                """, cancellationToken, change.RunId, change.Key!, (short)change.State,
                (object?)metric.StartedAt ?? DBNull.Value, (object?)metric.EndedAt ?? DBNull.Value);
        }
        // Dispatch inserts the new attempt after this event; there is no attempt row to update yet.
        if (change.Entity == EventEntity.Task && change.Progress is null && (ExecutionStatus)change.State != ExecutionStatus.Dispatched &&
            run.Snapshot.Metrics!.Tasks[change.Key!].Attempts is { Length: > 0 } attempts)
        {
            var attempt = attempts[^1];
            await ExecuteAsync(connection, """
                UPDATE task_attempts SET queue_wait_ms=$4,started_at=$5,ended_at=$6,queued_at=$8,
                    status=$7,failure_category=COALESCE($9,failure_category)
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                """, cancellationToken, change.RunId, change.Key!, (short)attempt.Attempt, attempt.QueueWaitMs,
                (object?)attempt.StartedAt ?? DBNull.Value, (object?)attempt.EndedAt ?? DBNull.Value,
                (short)(attempt.TerminalStatus ?? ((ExecutionStatus)change.State == ExecutionStatus.Running
                    ? ExecutionStatus.Running : ExecutionStatus.Dispatched)), attempt.QueuedAt,
                (object?)(short?)attempt.FailureCategory ?? DBNull.Value);
        }
        if (change.Entity == EventEntity.Run && TransitionTable.IsTerminal((RunStatus)change.State))
            await RollupWriter.RefreshAsync(connection, [change.RunId], cancellationToken);
    }

    internal static async Task ProjectTerminalAsync(NpgsqlConnection connection, RunState run, CancellationToken token)
    {
        await WriteSnapshotAsync(connection, run, token);
        foreach (var (key, metric) in run.Snapshot.Metrics!.Nodes)
            await ExecuteAsync(connection, """
                UPDATE node_runs SET status=$3,started_at=$4,ended_at=$5
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                """, token, run.Snapshot.RunId, key, (short)run.Snapshot.Nodes[key],
                (object?)metric.StartedAt ?? DBNull.Value, (object?)metric.EndedAt ?? DBNull.Value);
    }

    private static async Task WriteSnapshotAsync(NpgsqlConnection connection, RunState run, CancellationToken cancellationToken)
    {
        var snapshot = run.Snapshot;
        await ExecuteAsync(connection, """
            WITH saved AS (
                INSERT INTO run_snapshots(run_day,run_id,seq,created_at,payload)
                VALUES(uuid_v7_run_day($1),$1,$2,$3,$4) RETURNING seq
            )
            UPDATE runs SET last_seq=saved.seq,snapshot_seq=saved.seq,status=$5,ended_at=$6,started_at=$7 FROM saved
            WHERE run_day=uuid_v7_run_day($1) AND id=$1
            """, cancellationToken, snapshot.RunId, snapshot.LastSequence, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToUtf8Bytes(snapshot, RunStoreJson.Default.RunSnapshot), (short)snapshot.Status,
            (object?)snapshot.EndedAt ?? DBNull.Value, (object?)snapshot.Metrics!.Run.StartedAt ?? DBNull.Value);
    }

    public async Task<IReadOnlyList<(long Id, DispatchCommand Command)>> PendingAsync(AgentSession session,
        CancellationToken cancellationToken)
    {
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        var pending = await ReadPendingAsync(connection, session, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending;
    }

    internal async Task<(IReadOnlyList<(long Id, DispatchCommand Command)> Dispatches, IReadOnlyList<Cancel> Cancels)> PendingControlsAsync(
        AgentSession session, CancellationToken cancellationToken)
    {
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        var pending = await ReadPendingAsync(connection, session, cancellationToken);
        IReadOnlyList<Cancel> cancels = execution is null ? [] :
            await RunExecutionService.PendingCancelsAsync(connection, session.AgentId, session.SessionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (pending, cancels);
    }

    private static async Task<IReadOnlyList<(long Id, DispatchCommand Command)>> ReadPendingAsync(NpgsqlConnection connection,
        AgentSession session, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT o.id,o.payload FROM dispatch_outbox o JOIN agents a ON a.id=o.agent_id
            WHERE o.agent_id=$1 AND a.session_id=$2 AND a.status<>2 ORDER BY o.id LIMIT 100
            """, connection);
        command.Parameters.AddWithValue(session.AgentId);
        command.Parameters.AddWithValue(session.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pending = new List<(long, DispatchCommand)>();
        while (await reader.ReadAsync(cancellationToken))
            pending.Add((reader.GetInt64(0), DispatchCommand.Parser.ParseFrom(reader.GetFieldValue<byte[]>(1))));
        return pending;
    }

    public async Task<IReadOnlyList<Cancel>> PendingCancelsAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (execution is null) return [];
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        var pending = await RunExecutionService.PendingCancelsAsync(connection, session.AgentId, session.SessionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending;
    }

    public async Task AcceptAsync(AgentSession session, AgentMessage message, CancellationToken cancellationToken)
    {
        var progress = message.TaskProgress;
        if (progress is not null)
        {
            _ = Wf.Contracts.Progress.TaskProgressProtocol.Validate(progress);
            if (progress.At.ToDateTimeOffset() > UtcNow.AddMinutes(1))
                throw new ArgumentException("Task progress time is in the future.");
        }
        var reference = message.TaskStarted?.Task ?? message.TaskCompleted?.Task ?? progress?.Task;
        var identity = ResultDeduplicator.Identity(reference);
        var runId = identity.RunId;
        var at = (message.TaskStarted?.At ?? message.TaskCompleted?.At ?? progress?.At)?.ToDateTimeOffset()
            ?? throw new ArgumentException("Task event requires a timestamp.", nameof(message));
        var taskKey = identity.TaskKey;
        var completed = message.TaskCompleted;
        var reportedEpoch = message.TaskStarted?.ReservationEpoch ?? completed?.ReservationEpoch ?? progress?.ReservationEpoch ?? 0;
        if (completed is not null && TaskOutputJson.Error(completed.OutputsJson.Memory) is { } outputError)
            completed = TaskOutputJson.Reject(completed, outputError);
        if (completed is not null) ResultDeduplicator.Validate(completed);
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, ReservationManager.AllocationLock);
        long currentEpoch;
        await using (var fence = new NpgsqlCommand("SELECT session_id,reservation_epoch FROM agents WHERE id=$1 FOR UPDATE", connection))
        {
            fence.Parameters.AddWithValue(session.AgentId);
            await using var row = await fence.ExecuteReaderAsync(cancellationToken);
            if (!await row.ReadAsync(cancellationToken) || row.GetGuid(0) != session.SessionId)
                throw new InvalidOperationException("Agent connection was superseded by a new Hello.");
            currentEpoch = row.GetInt64(1);
        }
        var run = await LoadAsync(connection, runId, true, cancellationToken);
        await using (var attempt = new NpgsqlCommand("""
            SELECT status,reservation_epoch,NOT EXISTS(
                SELECT 1 FROM reservations r WHERE r.agent_id=$4 AND r.released_at IS NULL
                    AND (r.epoch<>task_attempts.reservation_epoch OR r.expires_at<=$5 OR r.release_requested_at IS NOT NULL))
            FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
              AND task_key=$2 AND attempt=$3 AND agent_id=$4 FOR UPDATE
            """, connection))
        {
            attempt.Parameters.AddWithValue(runId);
            attempt.Parameters.AddWithValue(taskKey);
            attempt.Parameters.AddWithValue(identity.Attempt);
            attempt.Parameters.AddWithValue(session.AgentId);
            attempt.Parameters.AddWithValue(UtcNow);
            await using var row = await attempt.ExecuteReaderAsync(cancellationToken);
            if (!await row.ReadAsync(cancellationToken))
                throw new ArgumentException("Task event does not match an attempt assigned to this agent.", nameof(message));
            var status = row.GetInt16(0);
            var assignedEpoch = row.GetInt64(1);
            if ((reportedEpoch == 0 ? assignedEpoch != 1 || currentEpoch != 1 :
                    reportedEpoch != (ulong)assignedEpoch || assignedEpoch != currentEpoch) || !row.GetBoolean(2))
            {
                LogStaleReservation(identity, session.AgentId, reportedEpoch);
                return;
            }
            if (progress is null && ResultDeduplicator.IsDuplicate((ExecutionStatus)status, completed is not null))
                return;
            if (progress is not null && (status is not (2 or 3) || TransitionTable.IsTerminal(run.Snapshot.Status) ||
                !run.Snapshot.Metrics!.Tasks.TryGetValue(taskKey, out var metric) ||
                metric.Attempts.Length == 0 || metric.Attempts[^1].Attempt != identity.Attempt))
            {
                logger.LogWarning("Discarded progress for inactive task {RunId}/{TaskKey}/{Attempt}.",
                    runId, taskKey, identity.Attempt);
                return;
            }
        }
        if (progress is not null)
        {
            await Events.TaskProgressProjector.AppendAsync(connection, run, progress, UtcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (completed is { OutputsJson.Length: > 0 })
        {
            // PostgreSQL jsonb rejects some otherwise valid JSON (NUL, invalid surrogates, out-of-range numbers).
            // Validate before recording success; roll back only this probe, never an accepted attempt transition.
            await transaction.SaveAsync("task_outputs", cancellationToken);
            try
            {
                await ExecuteAsync(connection, "SELECT $1::jsonb", cancellationToken, completed.OutputsJson.ToStringUtf8());
                await transaction.ReleaseAsync("task_outputs", cancellationToken);
            }
            catch (PostgresException error) when (error.SqlState is "22P02" or "22P05" or "22003")
            {
                await transaction.RollbackAsync("task_outputs", cancellationToken);
                completed = TaskOutputJson.Reject(completed, "Task outputs contain JSON values unsupported by output storage.");
            }
        }
        var target = completed?.Outcome switch
        {
            null => ExecutionStatus.Running,
            TaskOutcome.Succeeded => ExecutionStatus.Succeeded,
            TaskOutcome.Failed => ExecutionStatus.Failed,
            TaskOutcome.Cancelled => ExecutionStatus.Cancelled,
            TaskOutcome.TimedOut => ExecutionStatus.TimedOut,
            _ => throw new ArgumentException("Unsupported task outcome.", nameof(message))
        };
        // Only preserve explicit, known wire metadata. Older/foreign producers may omit it.
        var category = completed is not null && completed.Outcome != TaskOutcome.Succeeded &&
            Enum.TryParse<FailureCategory>(completed.Error?.Category, out var parsed) &&
            Enum.IsDefined(parsed) && completed.Error!.Category == parsed.ToString() ? parsed : (FailureCategory?)null;
        var completion = completed is not null && execution is not null
            ? await RunExecutionService.CompleteAsync(connection, run, completed, target, category, at, cancellationToken)
            : new RunExecutionService.Completion(target, target, null, category);
        var change = run.CreateEvent(EventEntity.Task, taskKey, (int)completion.Status, at) with
        {
            Attempt = identity.Attempt, FailureCategory = completion.Category,
            AttemptStatus = completion.Status == ExecutionStatus.Retrying ? completion.AttemptStatus : null,
            AttemptEndedAt = completion.Status == ExecutionStatus.Retrying ? at : null
        };
        await AppendAsync(connection, run, change, cancellationToken);
        await ExecuteAsync(connection, """
            WITH removed AS (
                DELETE FROM dispatch_outbox WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                RETURNING id
            )
            UPDATE task_attempts SET status=$4,
                started_at=CASE WHEN $4=3 THEN $5 ELSE started_at END,
                ended_at=CASE WHEN $4>=5 THEN $5 ELSE ended_at END,error=$6,outputs=$7::json,retry_at=$8,
                failure_category=COALESCE($9,failure_category)
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
            """, cancellationToken, runId, taskKey, identity.Attempt, (short)completion.AttemptStatus, at,
            (object?)completed?.Error?.Message ?? DBNull.Value,
            completed is { OutputsJson.Length: > 0 } &&
                !(completed.Outcome == TaskOutcome.Succeeded && completion.Status != ExecutionStatus.Succeeded)
                ? completed.OutputsJson.ToStringUtf8() : DBNull.Value,
            (object?)completion.RetryAt ?? DBNull.Value, (object?)(short?)completion.Category ?? DBNull.Value);
        if (completed is { OutputsJson.Length: > 0 } && completion.Status == ExecutionStatus.Succeeded)
            await ExecuteAsync(connection, """
                UPDATE node_runs SET outputs=COALESCE(outputs,'{}'::jsonb) || $3::jsonb
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                """, cancellationToken, runId, reference!.NodeKey, completed.OutputsJson.ToStringUtf8());
        await transaction.CommitAsync(cancellationToken);
    }

    internal void LogStaleReservation(Wf.Engine.Core.Results.ResultIdentity identity, Guid agentId, ulong epoch) =>
        logger.LogWarning("Discarded expired or fenced reservation epoch {Epoch} from agent {AgentId}, run {RunId}, task {TaskKey}, attempt {Attempt}.",
            epoch, agentId, identity.RunId, identity.TaskKey, identity.Attempt);

    internal static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken,
        params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

[JsonSerializable(typeof(RunSnapshot))]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(ImmutableDictionary<string, JsonElement>))]
[JsonSerializable(typeof(WorkflowMetadata))]
internal sealed partial class RunStoreJson : JsonSerializerContext;
