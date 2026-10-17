using Azure;
using Npgsql;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Engine.Maintenance;

public sealed class RetentionPurger(NpgsqlDataSource source, IBlobStore blobs, TimeProvider time,
    ILogger<RetentionPurger> logger, LeaseManager? leadership = null)
{
    public const int MaximumBatchSize = 5000;
    public const int MaximumTransactionSize = 100;
    public static bool IsOffPeak(DateTimeOffset now) => now.UtcDateTime.Hour is >= 2 and < 4;
    private sealed record ExpiredRun(DateOnly Day, Guid Id, string Workflow);

    public async Task<int> PurgeBatchAsync(CancellationToken token = default)
    {
        var removed = 0;
        while (removed < MaximumBatchSize)
        {
            var count = await PurgeTransactionAsync(Math.Min(MaximumTransactionSize, MaximumBatchSize - removed), token);
            removed += count;
            if (count == 0) break;
            await Task.Delay(TimeSpan.FromMilliseconds(50), time, token);
        }
        return removed;
    }

    private async Task<int> PurgeTransactionAsync(int maximum, CancellationToken token)
    {
        var now = time.GetUtcNow();
        if (!IsOffPeak(now)) return 0;
        var lease = leadership?.RequireLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await Configure(connection, token);
        if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, token);
        await LockPolicy(connection, token);
        var policy = await RetentionPolicyStore.ReadAsync(connection, token);
        var runs = new List<ExpiredRun>();
        await using (var select = new NpgsqlCommand("""
            SELECT r.run_day,r.id,r.workflow_id FROM runs r
            JOIN workflows w ON w.id=r.workflow_id JOIN workflow_types t ON t.id=w.type_id
            WHERE r.status BETWEEN 3 AND 6 AND r.ended_at IS NOT NULL
                AND r.created_at<$1-make_interval(days=>coalesce(t.retention_days,$2))
                AND r.ended_at<$1-make_interval(days=>coalesce(t.retention_days,$2))
                AND r.deadline_at<$1-interval '1 day 15 minutes'
                AND NOT EXISTS(SELECT 1 FROM task_attempts a WHERE a.run_day=r.run_day AND a.run_id=r.id
                    AND (a.status<5 OR a.deadline_at>=$1-interval '1 day 15 minutes'))
                AND NOT EXISTS(SELECT 1 FROM node_runs n WHERE n.run_day=r.run_day AND n.run_id=r.id AND n.status<5)
                AND NOT EXISTS(SELECT 1 FROM reservations q WHERE q.run_day=r.run_day AND q.run_id=r.id AND q.released_at IS NULL)
                AND NOT EXISTS(SELECT 1 FROM reservation_queue q WHERE q.run_day=r.run_day AND q.run_id=r.id)
                AND NOT EXISTS(SELECT 1 FROM dispatch_outbox d WHERE d.run_day=r.run_day AND d.run_id=r.id)
                AND NOT EXISTS(SELECT 1 FROM commands c WHERE c.run_id=r.id AND c.processed_at IS NULL)
                AND NOT EXISTS(SELECT 1 FROM artifact_grants g WHERE g.run_day=r.run_day AND g.run_id=r.id
                    AND NOT g.committed AND g.expires_at>=$1)
            ORDER BY r.run_day,r.id LIMIT $3 FOR UPDATE OF r SKIP LOCKED
            """, connection))
        {
            select.Parameters.AddWithValue(now);
            select.Parameters.AddWithValue(policy.DefaultDays);
            select.Parameters.AddWithValue(maximum);
            await using var reader = await select.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) runs.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetGuid(1), reader.GetString(2)));
        }
        if (runs.Count != 0)
        {
            var ids = runs.Select(run => run.Id).ToArray();
            var days = runs.Select(run => run.Day).Distinct().ToArray();
            await Execute(connection, """
                INSERT INTO retention_blob_deletions(kind,object_id)
                SELECT 0,id::text FROM unnest($1::uuid[]) id ON CONFLICT DO NOTHING
                """, token, ids);
            await Execute(connection, """
                INSERT INTO retention_blob_deletions(kind,object_id)
                SELECT 1,encode(sha256,'hex') FROM artifact_grants WHERE run_id=ANY($1) AND run_day=ANY($2)
                UNION SELECT 1,encode(sha256,'hex') FROM artifacts WHERE run_id=ANY($1) AND run_day=ANY($2)
                ON CONFLICT DO NOTHING
                """, token, ids, days);
            await Execute(connection, """
                WITH removed AS (DELETE FROM reports WHERE run_id=ANY($1) AND run_day=ANY($2) RETURNING id)
                DELETE FROM report_ids WHERE id IN(SELECT id FROM removed)
                """, token, ids, days);
            foreach (var table in new[] { "artifacts", "artifact_grants", "dispatch_outbox", "reservations",
                "reservation_queue", "task_attempts", "node_runs", "run_snapshots", "run_events" })
                await Execute(connection, $"DELETE FROM {table} WHERE run_id=ANY($1) AND run_day=ANY($2)", token, ids, days);
            await Execute(connection, "DELETE FROM commands WHERE run_id=ANY($1)", token, ids);
            await Execute(connection, "DELETE FROM runs WHERE id=ANY($1) AND run_day=ANY($2)", token, ids, days);
            foreach (var bucket in runs.Select(run => (run.Day, run.Workflow)).Distinct().OrderBy(b => b.Day)
                .ThenBy(b => b.Workflow, StringComparer.Ordinal))
            {
                await Execute(connection, """
                    SELECT pg_advisory_xact_lock(132853531,hashtext(($1::date-date '2000-01-01')::text||':'||$2))
                    """, token, bucket.Day, bucket.Workflow);
                await Execute(connection, "DELETE FROM daily_rollups WHERE day=$1 AND workflow_id=$2", token, bucket.Day, bucket.Workflow);
                await Execute(connection, "DELETE FROM dashboard_rollups WHERE day=$1 AND workflow_id=$2", token, bucket.Day, bucket.Workflow);
                await Execute(connection, "SELECT refresh_daily_rollups($1,$2)", token, bucket.Day, bucket.Workflow);
            }
        }
        await Execute(connection, """
            WITH expired AS (
                SELECT ts,id FROM audit_log WHERE ts<$1 ORDER BY ts,id LIMIT 5000 FOR UPDATE SKIP LOCKED
            )
            DELETE FROM audit_log a USING expired e WHERE a.ts=e.ts AND a.id=e.id
            """, token, now.AddDays(-policy.AuditDays));
        await transaction.CommitAsync(token);
        if (runs.Count != 0) logger.LogInformation("Retention removed {Count} expired runs; blob cleanup is durably queued.", runs.Count);
        return runs.Count;
    }

    public async Task<bool> DeletePendingBlobsAsync(CancellationToken token = default)
    {
        if (!IsOffPeak(time.GetUtcNow())) return false;
        leadership?.RequireLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await Configure(connection, token);
        // GC owns only a durable tombstone, not run state; never hold the one-second engine fence during blob IO.
        short kind;
        string id;
        await using (var next = new NpgsqlCommand("""
            SELECT kind,object_id FROM retention_blob_deletions WHERE next_attempt_at<=clock_timestamp()
            ORDER BY next_attempt_at,kind,object_id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection))
        {
            await using var reader = await next.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return false;
            kind = reader.GetInt16(0);
            id = reader.GetString(1);
        }
        bool referenced;
        if (kind == 0)
        {
            if (!BlobRetention.IsRun(id, out var run)) throw new InvalidDataException("Invalid queued retention run.");
            referenced = await Exists(connection, "SELECT EXISTS(SELECT 1 FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1)", token, run);
        }
        else if (kind == 1)
        {
            BlobRetention.ValidateArtifact(id);
            if (!await Exists(connection, "SELECT pg_try_advisory_xact_lock(1464226382,hashtext($1))", token, id))
            {
                await Execute(connection, """
                    UPDATE retention_blob_deletions SET next_attempt_at=clock_timestamp()+interval '10 seconds'
                    WHERE kind=$1 AND object_id=$2
                    """, token, kind, id);
                await transaction.CommitAsync(token);
                return true;
            }
            referenced = await Exists(connection, """
                SELECT EXISTS(SELECT 1 FROM artifacts WHERE sha256=decode($1,'hex'))
                    OR EXISTS(SELECT 1 FROM artifact_grants WHERE sha256=decode($1,'hex'))
                """, token, id);
        }
        else throw new InvalidDataException("Invalid queued retention object kind.");
        if (!referenced)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (kind == 0) await blobs.DeleteRunObjectsAsync(Guid.ParseExact(id, "D"), deadline.Token);
                else await blobs.DeleteArtifactAsync(id, deadline.Token);
            }
            catch (Exception error) when (error is IOException or RequestFailedException or NotSupportedException or UnauthorizedAccessException ||
                error is OperationCanceledException && !token.IsCancellationRequested)
            {
                logger.LogWarning(error, "Retention blob deletion failed; the durable queue will retry.");
                await Execute(connection, """
                    UPDATE retention_blob_deletions SET attempts=attempts+1,last_error='blob_delete_failed',
                        next_attempt_at=clock_timestamp()+make_interval(secs=>least(3600,60*power(2,least(attempts,6))))
                    WHERE kind=$1 AND object_id=$2
                    """, token, kind, id);
                await transaction.CommitAsync(token);
                return true;
            }
        }
        await Execute(connection, "DELETE FROM retention_blob_deletions WHERE kind=$1 AND object_id=$2", token, kind, id);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task QueueOrphanArtifactsAsync(CancellationToken token = default)
    {
        if (!IsOffPeak(time.GetUtcNow())) return;
        await foreach (var hash in blobs.ArtifactHashesAsync(token))
        {
            if (!IsOffPeak(time.GetUtcNow())) return;
            BlobRetention.ValidateArtifact(hash);
            var lease = leadership?.RequireLease();
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await Configure(connection, token);
            if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, token);
            await Execute(connection, """
                INSERT INTO retention_blob_deletions(kind,object_id)
                SELECT 1,$1 WHERE NOT EXISTS(SELECT 1 FROM artifacts WHERE sha256=decode($1,'hex'))
                    AND NOT EXISTS(SELECT 1 FROM artifact_grants WHERE sha256=decode($1,'hex'))
                ON CONFLICT DO NOTHING
                """, token, hash);
            await transaction.CommitAsync(token);
        }
    }

    internal static async Task Configure(NpgsqlConnection connection, CancellationToken token) =>
        await Execute(connection, "SET LOCAL lock_timeout='100ms'; SET LOCAL statement_timeout='5s'", token);

    internal static async Task LockPolicy(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT singleton FROM retention_config WHERE singleton FOR SHARE", connection);
        if (await command.ExecuteScalarAsync(token) is not true) throw new InvalidDataException("Retention singleton is missing.");
    }

    internal static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken token, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> Exists(NpgsqlConnection connection, string sql, CancellationToken token, object value)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(value);
        return await command.ExecuteScalarAsync(token) is true;
    }
}
