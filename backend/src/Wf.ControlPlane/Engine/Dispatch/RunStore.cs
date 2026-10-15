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
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core;
using Wf.Engine.Core.StateMachine;
using Wf.Engine.Core.Results;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.ControlPlane.Engine.Dispatch;

public sealed class RunStore(NpgsqlDataSource dataSource, ILogger<RunStore> logger, LeaseManager? leadership = null)
{
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
        Guid? seriesId = null, RunTrigger trigger = RunTrigger.Manual)
    {
        await ExecuteAsync(connection, "SELECT ensure_run_partitions(uuid_v7_run_day($1))", cancellationToken, run.Snapshot.RunId);
        await ExecuteAsync(connection, """
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,created_at,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$9,$10,$8,$6::jsonb,$7::jsonb,0,$4,$5)
            """, cancellationToken, run.Snapshot.RunId, workflowId, versionId, createdAt, deadline,
            JsonSerializer.Serialize(run.Snapshot.Variables, RunStoreJson.Default.ImmutableDictionaryStringJsonElement),
            JsonSerializer.Serialize(run.Snapshot.Metadata, RunStoreJson.Default.WorkflowMetadata), createdBy,
            (object?)seriesId ?? DBNull.Value, (short)trigger);
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

    internal async Task<RunState> LoadAsync(NpgsqlConnection connection, Guid runId, bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT s.payload FROM runs r JOIN run_snapshots s
              ON s.run_day=r.run_day AND s.run_id=r.id AND s.seq=r.snapshot_seq
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1
            """ + (forUpdate ? " FOR UPDATE OF r" : ""), connection);
        command.Parameters.AddWithValue(runId);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as byte[]
            ?? throw new KeyNotFoundException($"Run '{runId}' does not exist.");
        var snapshot = JsonSerializer.Deserialize(payload, RunStoreJson.Default.RunSnapshot)
            ?? throw new InvalidDataException("Persisted run snapshot is null.");
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
        return RunState.Restore(snapshot, events, message => logger.LogWarning("{RunStateError}", message));
    }

    public async Task AppendAsync(RunState run, EventEntity entity, string? key, int state, DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        var durable = await LoadAsync(connection, run.Snapshot.RunId, true, cancellationToken);
        if (durable.Snapshot.LastSequence != run.Snapshot.LastSequence)
            throw new InvalidOperationException("Run changed since it was loaded.");
        var change = durable.CreateEvent(entity, key, state, at);
        await AppendAsync(connection, durable, change, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        run.Apply(change);
    }

    internal static async Task AppendAsync(NpgsqlConnection connection, RunState run, RunEvent change,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            INSERT INTO run_events(run_day,run_id,seq,ts,kind,entity_key,payload)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,$5,$6)
            """, cancellationToken, change.RunId, change.Sequence, change.At, (short)change.Entity,
            (object?)change.Key ?? DBNull.Value, JsonSerializer.SerializeToUtf8Bytes(change, RunStoreJson.Default.RunEvent));
        run.Apply(change);
        await WriteSnapshotAsync(connection, run, cancellationToken);
        if (change.Entity == EventEntity.Node)
            await ExecuteAsync(connection, """
                UPDATE node_runs SET status=$3 WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                """, cancellationToken, change.RunId, change.Key!, (short)change.State);
    }

    private static async Task WriteSnapshotAsync(NpgsqlConnection connection, RunState run, CancellationToken cancellationToken)
    {
        var snapshot = run.Snapshot;
        await ExecuteAsync(connection, """
            WITH saved AS (
                INSERT INTO run_snapshots(run_day,run_id,seq,created_at,payload)
                VALUES(uuid_v7_run_day($1),$1,$2,$3,$4) RETURNING seq
            )
            UPDATE runs SET last_seq=saved.seq,snapshot_seq=saved.seq,status=$5,ended_at=$6 FROM saved
            WHERE run_day=uuid_v7_run_day($1) AND id=$1
            """, cancellationToken, snapshot.RunId, snapshot.LastSequence, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToUtf8Bytes(snapshot, RunStoreJson.Default.RunSnapshot), (short)snapshot.Status,
            (object?)snapshot.EndedAt ?? DBNull.Value);
    }

    public async Task<IReadOnlyList<(long Id, DispatchCommand Command)>> PendingAsync(AgentSession session,
        CancellationToken cancellationToken)
    {
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT o.id,o.payload FROM dispatch_outbox o JOIN agents a ON a.id=o.agent_id
            WHERE o.agent_id=$1 AND a.session_id=$2 ORDER BY o.id LIMIT 100
            """, connection, transaction);
        command.Parameters.AddWithValue(session.AgentId);
        command.Parameters.AddWithValue(session.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pending = new List<(long, DispatchCommand)>();
        while (await reader.ReadAsync(cancellationToken))
            pending.Add((reader.GetInt64(0), DispatchCommand.Parser.ParseFrom(reader.GetFieldValue<byte[]>(1))));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return pending;
    }

    public async Task AcceptAsync(AgentSession session, AgentMessage message, CancellationToken cancellationToken)
    {
        var reference = message.TaskStarted?.Task ?? message.TaskCompleted?.Task;
        var identity = ResultDeduplicator.Identity(reference);
        var runId = identity.RunId;
        var at = (message.TaskStarted?.At ?? message.TaskCompleted?.At)?.ToDateTimeOffset()
            ?? throw new ArgumentException("Task event requires a timestamp.", nameof(message));
        var taskKey = identity.TaskKey;
        var completed = message.TaskCompleted;
        if (completed is not null) ResultDeduplicator.Validate(completed);
        var lease = CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceAsync(connection, transaction, lease, cancellationToken);
        await using (var fence = new NpgsqlCommand("SELECT session_id FROM agents WHERE id=$1 FOR UPDATE", connection))
        {
            fence.Parameters.AddWithValue(session.AgentId);
            if (await fence.ExecuteScalarAsync(cancellationToken) is not Guid current || current != session.SessionId)
                throw new InvalidOperationException("Agent connection was superseded by a new Hello.");
        }
        var run = await LoadAsync(connection, runId, true, cancellationToken);
        await using (var attempt = new NpgsqlCommand("""
            SELECT status FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
              AND task_key=$2 AND attempt=$3 AND agent_id=$4 FOR UPDATE
            """, connection))
        {
            attempt.Parameters.AddWithValue(runId);
            attempt.Parameters.AddWithValue(taskKey);
            attempt.Parameters.AddWithValue(identity.Attempt);
            attempt.Parameters.AddWithValue(session.AgentId);
            if (await attempt.ExecuteScalarAsync(cancellationToken) is not short status)
                throw new ArgumentException("Task event does not match an attempt assigned to this agent.", nameof(message));
            if (ResultDeduplicator.IsDuplicate((ExecutionStatus)status, completed is not null))
                return;
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
        var change = run.CreateEvent(EventEntity.Task, taskKey, (int)target, at);
        await AppendAsync(connection, run, change, cancellationToken);
        await ExecuteAsync(connection, """
            WITH removed AS (
                DELETE FROM dispatch_outbox WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                RETURNING id
            )
            UPDATE task_attempts SET status=$4,
                started_at=CASE WHEN $4=3 THEN $5 ELSE started_at END,
                ended_at=CASE WHEN $4>=5 THEN $5 ELSE ended_at END,error=$6
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
            """, cancellationToken, runId, taskKey, identity.Attempt, (short)target, at,
            (object?)completed?.Error?.Message ?? DBNull.Value);
        if (completed is { Outcome: TaskOutcome.Succeeded, OutputsJson.Length: > 0 })
            await ExecuteAsync(connection, """
                UPDATE node_runs SET outputs=COALESCE(outputs,'{}'::jsonb) || $3::jsonb
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                """, cancellationToken, runId, reference!.NodeKey, completed.OutputsJson.ToStringUtf8());
        await transaction.CommitAsync(cancellationToken);
    }

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
