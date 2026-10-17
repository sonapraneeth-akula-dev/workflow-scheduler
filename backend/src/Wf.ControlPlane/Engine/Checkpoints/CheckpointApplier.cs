using System.Text.Json;
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
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        short attemptStatus;
        RunStatus runStatus;
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
            assignment.Parameters.AddWithValue(checkpoint.RunId);
            assignment.Parameters.AddWithValue(checkpoint.TaskKey);
            assignment.Parameters.AddWithValue(checkpoint.Attempt);
            assignment.Parameters.AddWithValue(session.AgentId);
            assignment.Parameters.AddWithValue(runs.UtcNow);
            await using var row = await assignment.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetGuid(0) != session.SessionId || row.IsDBNull(3) ||
                Wf.Contracts.Agent.V1.Dispatch.Parser.ParseFrom(row.GetFieldValue<byte[]>(3)).ReservationEpoch != checkpoint.ReservationEpoch)
                throw new ArgumentException("Checkpoint does not match its assigned agent session and dispatch.");
            if (row.GetInt64(1) != (long)checkpoint.ReservationEpoch ||
                row.GetInt64(2) != (long)checkpoint.ReservationEpoch || !row.GetBoolean(6) || !row.GetBoolean(7))
            {
                logger.LogWarning("Discarded stale checkpoint for run {RunId}, task {TaskKey}, attempt {Attempt}.",
                    checkpoint.RunId, checkpoint.TaskKey, checkpoint.Attempt);
                return;
            }
            attemptStatus = row.GetInt16(4);
            runStatus = (RunStatus)row.GetInt16(5);
        }
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
                if (sequence > checkpoint.Sequence) return;
                if (sequence == checkpoint.Sequence)
                {
                    if (row.GetFieldValue<DateTimeOffset>(1) != checkpoint.SavedAt || row.GetInt16(2) != checkpoint.Attempt ||
                        row.GetString(3) != checkpoint.BlobKey || row.GetString(4) != checkpoint.Sha256 ||
                        row.GetInt64(5) != checkpoint.SizeBytes)
                        throw new ArgumentException("Checkpoint sequence was already committed with different content.");
                    return;
                }
                previousAt = row.GetFieldValue<DateTimeOffset>(1);
            }
        }
        if (runStatus is not (RunStatus.Running or RunStatus.Aborting) || attemptStatus is not (2 or 3))
            throw new ArgumentException("A new checkpoint requires a current active attempt.");
        if (previousAt is { } previous && checkpoint.SavedAt - previous < CheckpointProtocol.MinimumInterval)
            throw new ArgumentException("Checkpoint saves must be at least five seconds apart.");
        using var state = new MemoryStream();
        await blobs.DownloadAsync(new(BlobKind.Checkpoints, checkpoint.Sha256, checkpoint.SizeBytes, checkpoint.BlobKey), state, token);
        try { CheckpointProtocol.ValidateState(state.GetBuffer().AsMemory(0, checked((int)state.Length))); }
        catch (JsonException error) { throw new ArgumentException("Checkpoint state is not valid JSON.", nameof(saved), error); }
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
}
