using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Google.Protobuf;
using Npgsql;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core.Results;

namespace Wf.ControlPlane.Live;

public sealed class LiveLogSink(NpgsqlDataSource source, RunStore runs, IBlobStore blobs, LiveLogRelay relay,
    ILogger<LiveLogSink> logger)
{
    public async Task AcceptAsync(AgentSession session, LiveLogBatch batch, CancellationToken token)
    {
        LiveLogProtocol.Validate(batch);
        var identity = ResultDeduplicator.Identity(batch.Task);
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        long epoch;
        await using (var command = new NpgsqlCommand("SELECT session_id,reservation_epoch FROM agents WHERE id=$1 FOR UPDATE", connection))
        {
            command.Parameters.AddWithValue(session.AgentId);
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetGuid(0) != session.SessionId)
                throw new ArgumentException("Live log agent session was superseded.");
            epoch = row.GetInt64(1);
        }
        await using (var command = new NpgsqlCommand(
            "SELECT id FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1 FOR UPDATE", connection))
        {
            command.Parameters.AddWithValue(identity.RunId);
            if (await command.ExecuteScalarAsync(token) is null) throw new ArgumentException("Live log run is unavailable.");
        }
        await using (var command = new NpgsqlCommand("""
            SELECT t.reservation_epoch,t.dispatch_payload,NOT EXISTS(
                SELECT 1 FROM reservations r WHERE r.agent_id=$4 AND r.released_at IS NULL
                    AND (r.epoch<>t.reservation_epoch OR r.expires_at<=$5 OR r.release_requested_at IS NOT NULL))
            FROM task_attempts t
            WHERE t.run_day=uuid_v7_run_day($1) AND t.run_id=$1 AND t.task_key=$2 AND t.attempt=$3 AND t.agent_id=$4
            FOR UPDATE
            """, connection))
        {
            command.Parameters.AddWithValue(identity.RunId);
            command.Parameters.AddWithValue(identity.TaskKey);
            command.Parameters.AddWithValue(identity.Attempt);
            command.Parameters.AddWithValue(session.AgentId);
            command.Parameters.AddWithValue(runs.UtcNow);
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.IsDBNull(1) ||
                Dispatch.Parser.ParseFrom(row.GetFieldValue<byte[]>(1)).ReservationEpoch != batch.ReservationEpoch)
                throw new ArgumentException("Live logs do not match the assigned attempt.");
            if (batch.ReservationEpoch != (ulong)epoch || row.GetInt64(0) != epoch || !row.GetBoolean(2))
            {
                runs.LogStaleReservation(identity, session.AgentId, batch.ReservationEpoch);
                return;
            }
        }
        if (!relay.HasInterest(batch.Task)) return;
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
            batch.WriteTo(gzip);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        var key = LogIndex.PrefixFor(batch.Task) + "live-" + sha + ".log.gz";
        var channel = "live_log_batches";
        var notification = JsonSerializer.Serialize(new LiveLogPointer(LiveLogProtocol.Identity(batch.Task),
            key, sha, bytes.Length), LiveLogJson.Default.LiveLogPointer);
        try
        {
            relay.Retire(key);
            bytes.Position = 0;
            await blobs.UploadAsync(new(BlobKind.Logs, sha, bytes.Length, key), bytes, token);
        }
        catch (Exception error) when (error is IOException or RequestFailedException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Live log upload failed; requesting archived-log resynchronization without interrupting task results.");
            channel = "live_log_index";
            notification = LogIndex.PrefixFor(batch.Task);
        }
        await using var notify = new NpgsqlCommand("SELECT pg_notify($1,$2)", connection);
        notify.Parameters.AddWithValue(channel);
        notify.Parameters.AddWithValue(notification);
        await notify.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }
}
