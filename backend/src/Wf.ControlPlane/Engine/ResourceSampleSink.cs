using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Resources;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core.Results;
using Wf.Engine.Core.StateMachine;

namespace Wf.ControlPlane.Engine;

public sealed class ResourceSampleSink(NpgsqlDataSource source, RunStore runs, IBlobStore blobs)
{
    public async Task AcceptAsync(AgentSession session, ResourceSample sample, CancellationToken token)
    {
        var identity = ResultDeduplicator.Identity(sample.Task);
        if (identity.RunId.Version != 7 || sample.ReservationEpoch == 0 || sample.SampleSequence is 0 or > long.MaxValue)
            throw new ArgumentException("Invalid resource attempt reservation or ordinal.");
        var observation = ResourceContract.Parse(sample.SampleJson.Memory);
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        long currentEpoch;
        await using (var fence = new NpgsqlCommand("SELECT session_id,reservation_epoch FROM agents WHERE id=$1 FOR UPDATE", connection))
        {
            fence.Parameters.AddWithValue(session.AgentId);
            await using var row = await fence.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetGuid(0) != session.SessionId)
                throw new ArgumentException("Resource sample agent session was superseded.");
            currentEpoch = row.GetInt64(1);
        }
        await using (var run = new NpgsqlCommand("SELECT id FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1 FOR UPDATE", connection))
        {
            run.Parameters.AddWithValue(identity.RunId);
            if (await run.ExecuteScalarAsync(token) is null) throw new ArgumentException("Resource run is unavailable.");
        }
        long count;
        short status;
        ResourcePointer? previous;
        await using (var query = new NpgsqlCommand("""
            SELECT resource_sample_count,resource_series_uri,resource_series_sha256,resource_series_bytes,
                   dispatch_payload,status,reservation_epoch,NOT EXISTS(
                       SELECT 1 FROM reservations r WHERE r.agent_id=$4 AND r.released_at IS NULL
                           AND (r.epoch<>task_attempts.reservation_epoch OR r.expires_at<=$5 OR r.release_requested_at IS NOT NULL))
            FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3 AND agent_id=$4
            FOR UPDATE
            """, connection))
        {
            query.Parameters.AddWithValue(identity.RunId);
            query.Parameters.AddWithValue(identity.TaskKey);
            query.Parameters.AddWithValue(identity.Attempt);
            query.Parameters.AddWithValue(session.AgentId);
            query.Parameters.AddWithValue(runs.UtcNow);
            await using var row = await query.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.IsDBNull(4) ||
                Wf.Contracts.Agent.V1.Dispatch.Parser.ParseFrom(row.GetFieldValue<byte[]>(4)).ReservationEpoch != sample.ReservationEpoch)
                throw new ArgumentException("Resource sample does not match the assigned attempt reservation.");
            if (sample.ReservationEpoch != (ulong)row.GetInt64(6) || row.GetInt64(6) != currentEpoch || !row.GetBoolean(7))
            {
                runs.LogStaleReservation(identity, session.AgentId, sample.ReservationEpoch);
                return;
            }
            count = row.GetInt64(0);
            previous = row.IsDBNull(1) ? null : new(row.GetString(1), row.GetString(2), row.GetInt64(3));
            status = row.GetInt16(5);
        }
        if (sample.SampleSequence <= (ulong)count)
        {
            // Replayed journal records must match the immutable historical observation.
            var pointer = previous;
            for (var ordinal = (ulong)count; ordinal >= sample.SampleSequence; ordinal--)
            {
                var old = await ReadAsync(pointer ?? throw new InvalidDataException("Incomplete resource chain."), token);
                if (old.Sequence != ordinal) throw new InvalidDataException("Invalid resource chain ordinal.");
                if (ordinal == sample.SampleSequence)
                {
                    if (old.Observation != observation) throw new ArgumentException("Resource sample replay conflicts.");
                    await transaction.CommitAsync(token);
                    return;
                }
                pointer = old.Previous;
            }
        }
        if (TransitionTable.IsTerminal((ExecutionStatus)status))
        {
            runs.LogInactiveResource(identity, session.AgentId, sample.SampleSequence, status);
            await transaction.CommitAsync(token);
            return;
        }
        if (status is not (2 or 3 or 4) || sample.SampleSequence != checked((ulong)count + 1))
            throw new ArgumentException($"Resource sample {sample.SampleSequence} is out of order (expected {count + 1}) " +
                $"or the attempt is terminal (status {status}).");
        var point = observation.Task;
        if (previous is null && (point.IntervalSeconds is not null || point.CpuPercent is not null || point.NetworkSentDeltaBytes is not null))
            throw new ArgumentException("The first resource sample must establish a baseline, not invent rates.");
        if (previous is not null)
        {
            var old = await ReadAsync(previous, token);
            var baseline = old.Observation.Task;
            if (point.NetworkSentDeltaBytes is { } delta &&
                (baseline.NetworkSentBytes is not { } sent || baseline.NetworkReceivedBytes is not { } received ||
                 point.NetworkScope != baseline.NetworkScope || point.NetworkSentBytes < sent || point.NetworkReceivedBytes < received ||
                 point.NetworkSentBytes - sent != delta || point.NetworkReceivedBytes - received != point.NetworkReceivedDeltaBytes))
                throw new ArgumentException("Resource byte deltas require matching available counters, not a reset or unavailable baseline.");
        }
        var seconds = point.IntervalSeconds ?? 0;
        var cpuSeconds = point.CpuPercent is null ? 0 : seconds;
        var weighted = (point.CpuPercent ?? 0) * cpuSeconds;
        if (!double.IsFinite(weighted)) throw new ArgumentException("Resource CPU duration is not finite.");
        var chunk = new ResourceChunk(sample.SampleSequence, observation, previous);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(chunk, ResourceJsonContext.Default.ResourceChunk);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = $"resources/{identity.RunId:D}/{TaskHash(identity.TaskKey)}/{identity.Attempt}/{sample.SampleSequence}-{sha}.json";
        var content = new BlobContent(BlobKind.Resources, sha, bytes.Length, key);
        await blobs.UploadAsync(content, new MemoryStream(bytes, writable: false), token);
        await using var update = new NpgsqlCommand("""
            UPDATE task_attempts SET resource_sample_count=$4,resource_series_uri=$5,resource_series_sha256=$6,resource_series_bytes=$7,
                resource_cpu_percent_seconds=resource_cpu_percent_seconds+$8,
                resource_cpu_observed_seconds=resource_cpu_observed_seconds+$9,
                resource_avg_cpu_percent=CASE WHEN resource_cpu_observed_seconds+$9>0
                    THEN (resource_cpu_percent_seconds+$8)/(resource_cpu_observed_seconds+$9) ELSE NULL END,
                resource_max_cpu_percent=GREATEST(resource_max_cpu_percent,$10),
                resource_peak_ram_bytes=GREATEST(resource_peak_ram_bytes,$11),
                resource_network_sent_bytes=CASE WHEN $12::numeric IS NULL THEN resource_network_sent_bytes
                    ELSE COALESCE(resource_network_sent_bytes,0)+$12 END,
                resource_network_received_bytes=CASE WHEN $13::numeric IS NULL THEN resource_network_received_bytes
                    ELSE COALESCE(resource_network_received_bytes,0)+$13 END,
                resource_network_observed_seconds=resource_network_observed_seconds+$14
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
            """, connection);
        update.Parameters.AddWithValue(identity.RunId);
        update.Parameters.AddWithValue(identity.TaskKey);
        update.Parameters.AddWithValue(identity.Attempt);
        update.Parameters.AddWithValue(checked((long)sample.SampleSequence));
        update.Parameters.AddWithValue(key);
        update.Parameters.AddWithValue(sha);
        update.Parameters.AddWithValue((long)bytes.Length);
        update.Parameters.AddWithValue(weighted);
        update.Parameters.AddWithValue(cpuSeconds);
        update.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = (object?)point.CpuPercent ?? DBNull.Value });
        foreach (var number in new[] { point.RamBytes, point.NetworkSentDeltaBytes, point.NetworkReceivedDeltaBytes })
            update.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric,
                Value = number is { } value ? (decimal)value : DBNull.Value });
        update.Parameters.AddWithValue(point.NetworkSentDeltaBytes is null ? 0 : seconds);
        await update.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<ResourceChunk> ReadAsync(ResourcePointer pointer, CancellationToken token)
    {
        if (pointer.SizeBytes is <= 0 or > 32768) throw new InvalidDataException("Invalid resource object size.");
        using var stream = new MemoryStream();
        await blobs.DownloadAsync(new BlobContent(BlobKind.Resources, pointer.Sha256, pointer.SizeBytes, pointer.Key), stream, token);
        var chunk = JsonSerializer.Deserialize(stream.GetBuffer().AsSpan(0, (int)stream.Length), ResourceJsonContext.Default.ResourceChunk)
            ?? throw new InvalidDataException("Invalid resource series object.");
        if (chunk.SchemaVersion != 1 || chunk.Sequence is 0 or > long.MaxValue || chunk.Observation?.Task is null)
            throw new InvalidDataException("Unsupported resource series schema.");
        return chunk;
    }

    public static string TaskHash(string taskKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(taskKey)));
}
