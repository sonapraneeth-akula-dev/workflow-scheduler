using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Engine.Checkpoints;

public sealed class CheckpointApplier(NpgsqlDataSource source, RunStore runs, IBlobStore blobs,
    ILogger<CheckpointApplier> logger)
{
    public async Task AcceptAsync(AgentSession session, CheckpointSaved saved, CancellationToken token = default)
    {
        var checkpoint = CheckpointProtocol.Validate(saved);
        if (checkpoint.SavedAt > runs.UtcNow.AddMinutes(1))
            throw new ArgumentException("Checkpoint time is in the future.", nameof(saved));
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using (var validation = await connection.BeginTransactionAsync(token))
        {
            await runs.FenceAsync(connection, validation, lease, token);
            if (!await CanAdvanceAsync(connection, session, checkpoint, token)) return;
            await validation.CommitAsync(token);
        }
        using var state = new MemoryStream();
        await blobs.DownloadAsync(new(BlobKind.Checkpoints, checkpoint.Sha256, checkpoint.SizeBytes, checkpoint.BlobKey), state, token);
        try { CheckpointProtocol.ValidateState(state.GetBuffer().AsMemory(0, checked((int)state.Length))); }
        catch (JsonException error) { throw new ArgumentException("Checkpoint state is not valid JSON.", nameof(saved), error); }
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        if (!await CanAdvanceAsync(connection, session, checkpoint, token)) return;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO task_checkpoints(run_day,run_id,task_key,attempt,sequence,saved_at,blob_uri,sha256,size_bytes)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT(run_day,run_id,task_key) DO UPDATE SET attempt=excluded.attempt,sequence=excluded.sequence,
                saved_at=excluded.saved_at,blob_uri=excluded.blob_uri,sha256=excluded.sha256,size_bytes=excluded.size_bytes
            WHERE task_checkpoints.sequence<excluded.sequence
            """, connection))
        {
            insert.Parameters.AddWithValue(checkpoint.RunId);
            insert.Parameters.AddWithValue(checkpoint.TaskKey);
            insert.Parameters.AddWithValue(checkpoint.Attempt);
            insert.Parameters.AddWithValue(checkpoint.Sequence);
            insert.Parameters.AddWithValue(checkpoint.SavedAt);
            insert.Parameters.AddWithValue(checkpoint.BlobKey);
            insert.Parameters.AddWithValue(checkpoint.Sha256);
            insert.Parameters.AddWithValue(checkpoint.SizeBytes);
            await insert.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private async Task<bool> CanAdvanceAsync(NpgsqlConnection connection, AgentSession session,
        CheckpointReference checkpoint, CancellationToken token)
    {
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        var assignment = await LockAssignment(connection, session, checkpoint.RunId, checkpoint.TaskKey,
            checkpoint.Attempt, checkpoint.ReservationEpoch, token);
        if (assignment is null) return false;
        var (attemptStatus, runStatus) = assignment.Value;
        DateTimeOffset? previousAt = null;
        await using (var latest = new NpgsqlCommand("""
            SELECT sequence,saved_at,attempt,blob_uri,sha256,size_bytes FROM task_checkpoints
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 FOR UPDATE
            """, connection))
        {
            latest.Parameters.AddWithValue(checkpoint.RunId);
            latest.Parameters.AddWithValue(checkpoint.TaskKey);
            await using var row = await latest.ExecuteReaderAsync(token);
            if (await row.ReadAsync(token))
            {
                var sequence = row.GetInt64(0);
                if (sequence > checkpoint.Sequence) return false;
                if (sequence == checkpoint.Sequence)
                {
                    if (row.GetFieldValue<DateTimeOffset>(1) != checkpoint.SavedAt || row.GetInt16(2) != checkpoint.Attempt ||
                        row.GetString(3) != checkpoint.BlobKey || row.GetString(4) != checkpoint.Sha256 ||
                        row.GetInt64(5) != checkpoint.SizeBytes)
                        throw new ArgumentException("Checkpoint sequence was already committed with different content.");
                    return false;
                }
                previousAt = row.GetFieldValue<DateTimeOffset>(1);
            }
        }
        if (runStatus is not (RunStatus.Running or RunStatus.Aborting) || attemptStatus is not (2 or 3))
            throw new ArgumentException("A new checkpoint requires a current active attempt.");
        if (previousAt is { } previous && checkpoint.SavedAt - previous < CheckpointProtocol.MinimumInterval)
            throw new ArgumentException("Checkpoint saves must be at least five seconds apart.");
        return true;
    }

    public async Task UploadAsync(AgentSession session, CheckpointSaved saved, Stream body, CancellationToken token = default)
    {
        var checkpoint = CheckpointProtocol.Validate(saved);
        if (checkpoint.SavedAt > runs.UtcNow.AddMinutes(1))
            throw new ArgumentException("Checkpoint time is in the future.", nameof(saved));
        var bytes = new byte[checked((int)checkpoint.SizeBytes) + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await body.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count != checkpoint.SizeBytes) throw new InvalidDataException("Checkpoint length does not match its metadata.");
        CheckpointProtocol.ValidateState(bytes.AsMemory(0, count));
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await Authorize();
        using var input = new MemoryStream(bytes, 0, count, writable: false);
        await blobs.UploadAsync(new(BlobKind.Checkpoints, checkpoint.Sha256, checkpoint.SizeBytes, checkpoint.BlobKey), input, token);
        await Authorize();

        async Task Authorize()
        {
            await using var transaction = await connection.BeginTransactionAsync(token);
            await runs.FenceAsync(connection, transaction, lease, token);
            await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
            (short Attempt, RunStatus Run)? assignment;
            try
            {
                assignment = await LockAssignment(connection, session, checkpoint.RunId, checkpoint.TaskKey,
                    checkpoint.Attempt, checkpoint.ReservationEpoch, token);
            }
            catch (ArgumentException error)
            { throw new UnauthorizedAccessException("Checkpoint upload does not match the current assignment.", error); }
            if (assignment is not { } active || active.Run is not (RunStatus.Running or RunStatus.Aborting) ||
                active.Attempt is not (2 or 3))
                throw new UnauthorizedAccessException("Checkpoint upload requires the current active attempt.");
            await transaction.CommitAsync(token);
        }
    }

    public async Task<CheckpointSeed?> LoadAsync(AttemptRef target, CancellationToken token = default)
    {
        if (target.Attempt <= 1) return null;
        var seed = new CheckpointSeed();
        await using (var command = source.CreateCommand("""
            SELECT c.attempt,c.sequence,c.saved_at,c.blob_uri,c.sha256,c.size_bytes,a.reservation_epoch
            FROM task_checkpoints c JOIN task_attempts a
                ON a.run_day=c.run_day AND a.run_id=c.run_id AND a.task_key=c.task_key AND a.attempt=c.attempt
            WHERE c.run_day=uuid_v7_run_day($1) AND c.run_id=$1 AND c.task_key=$2
            """))
        {
            command.Parameters.AddWithValue(Guid.Parse(target.RunId));
            command.Parameters.AddWithValue($"{target.NodeKey}/{target.TaskKey}");
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetInt16(0) >= target.Attempt) return null;
            var original = target.Clone();
            original.Attempt = checked((uint)row.GetInt16(0));
            seed.Reference = new()
            {
                Task = original, CheckpointSequence = checked((ulong)row.GetInt64(1)),
                SavedAt = Timestamp.FromDateTimeOffset(row.GetFieldValue<DateTimeOffset>(2)),
                BlobKey = row.GetString(3), Sha256 = row.GetString(4), SizeBytes = checked((ulong)row.GetInt64(5)),
                ReservationEpoch = checked((ulong)row.GetInt64(6))
            };
        }
        var reference = CheckpointProtocol.Validate(seed.Reference);
        using var state = new MemoryStream();
        await blobs.DownloadAsync(new(BlobKind.Checkpoints, reference.Sha256, reference.SizeBytes, reference.BlobKey), state, token);
        seed.StateJson = ByteString.CopyFrom(state.GetBuffer(), 0, checked((int)state.Length));
        _ = CheckpointProtocol.ValidateSeed(seed, target);
        return seed;
    }

    public async Task<CheckpointSeed?> LoadForDispatchAsync(Wf.Contracts.Agent.V1.Dispatch dispatch,
        AgentSession session, CancellationToken token = default)
    {
        var seed = await LoadAsync(dispatch.Task, token);
        if (seed is null) return null;
        var runId = Guid.Parse(dispatch.Task.RunId);
        var taskKey = $"{dispatch.Task.NodeKey}/{dispatch.Task.TaskKey}";
        var attempt = checked((short)dispatch.Task.Attempt);
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        var assignment = await LockAssignment(connection, session, runId, taskKey, attempt, dispatch.ReservationEpoch, token);
        if (assignment is not { } current || current.Run is not (RunStatus.Running or RunStatus.Aborting) ||
            current.Attempt is not (2 or 3))
            throw new InvalidDataException("Checkpoint resume dispatch is no longer current.");
        await using var record = new NpgsqlCommand("""
            UPDATE task_attempts SET resumed_from_checkpoint_at=$4
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                AND (resumed_from_checkpoint_at IS NULL OR resumed_from_checkpoint_at=$4)
            """, connection);
        record.Parameters.AddWithValue(runId);
        record.Parameters.AddWithValue(taskKey);
        record.Parameters.AddWithValue(attempt);
        record.Parameters.AddWithValue(seed.Reference.SavedAt.ToDateTimeOffset());
        if (await record.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidDataException("Attempt checkpoint resume attribution conflicts with its original seed.");
        await transaction.CommitAsync(token);
        return seed;
    }

    private async Task<(short Attempt, RunStatus Run)?> LockAssignment(NpgsqlConnection connection,
        AgentSession session, Guid runId, string taskKey, short attempt, ulong reservationEpoch, CancellationToken token)
    {
        await using (var assignment = new NpgsqlCommand("""
            SELECT g.session_id,g.reservation_epoch,a.reservation_epoch,a.dispatch_payload,a.status,r.status,
                NOT EXISTS(SELECT 1 FROM task_attempts newer WHERE newer.run_day=a.run_day AND newer.run_id=a.run_id
                    AND newer.task_key=a.task_key AND newer.attempt>a.attempt),
                NOT EXISTS(SELECT 1 FROM reservations q WHERE q.agent_id=g.id AND q.released_at IS NULL
                    AND (q.epoch<>a.reservation_epoch OR q.expires_at<=$5 OR q.release_requested_at IS NOT NULL))
            FROM task_attempts a JOIN agents g ON g.id=a.agent_id JOIN runs r ON r.run_day=a.run_day AND r.id=a.run_id
            WHERE a.run_day=uuid_v7_run_day($1) AND a.run_id=$1 AND a.task_key=$2 AND a.attempt=$3 AND a.agent_id=$4
            FOR UPDATE OF g,r,a
            """, connection))
        {
            assignment.Parameters.AddWithValue(runId);
            assignment.Parameters.AddWithValue(taskKey);
            assignment.Parameters.AddWithValue(attempt);
            assignment.Parameters.AddWithValue(session.AgentId);
            assignment.Parameters.AddWithValue(runs.UtcNow);
            await using var row = await assignment.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetGuid(0) != session.SessionId || row.IsDBNull(3) ||
                Wf.Contracts.Agent.V1.Dispatch.Parser.ParseFrom(row.GetFieldValue<byte[]>(3)).ReservationEpoch != reservationEpoch)
                throw new ArgumentException("Checkpoint does not match its assigned agent session and dispatch.");
            if (row.GetInt64(1) != (long)reservationEpoch ||
                row.GetInt64(2) != (long)reservationEpoch || !row.GetBoolean(6) || !row.GetBoolean(7))
            {
                logger.LogWarning("Discarded stale checkpoint for run {RunId}, task {TaskKey}, attempt {Attempt}.",
                    runId, taskKey, attempt);
                return null;
            }
            return (row.GetInt16(4), (RunStatus)row.GetInt16(5));
        }
    }
}
