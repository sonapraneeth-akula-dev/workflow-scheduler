using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core;
using Wf.Engine.Core.StateMachine;

namespace Wf.ControlPlane.Engine.Reservations;

public sealed class ReservationLeaseService(NpgsqlDataSource dataSource, RunStore runs, TimeProvider time,
    ILogger<ReservationLeaseService> logger) : BackgroundService
{
    public async Task<bool> RenewAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default) =>
        await MaintainAsync(runId, nodeKey, true, null, cancellationToken);

    public async Task ReleaseAsync(Guid runId, string nodeKey, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 500) throw new ArgumentOutOfRangeException(nameof(reason));
        await MaintainAsync(runId, nodeKey, false, reason, cancellationToken);
    }

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var owners = new List<(Guid RunId, string NodeKey)>();
        await using (var query = dataSource.CreateCommand("""
            SELECT DISTINCT run_id,node_key FROM reservations
            WHERE released_at IS NULL AND release_requested_at IS NULL ORDER BY run_id,node_key
            """))
        {
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) owners.Add((reader.GetGuid(0), reader.GetString(1)));
        }
        foreach (var owner in owners)
            await MaintainAsync(owner.RunId, owner.NodeKey, false, null, cancellationToken);
    }

    private async Task<bool> MaintainAsync(Guid runId, string nodeKey, bool forceRenew, string? reason,
        CancellationToken cancellationToken)
    {
        if (runId.Version != 7 || string.IsNullOrWhiteSpace(nodeKey) || nodeKey.Contains('/'))
            throw new ArgumentException("Reservation requires a UUIDv7 run and a node key.");
        var lease = runs.CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, lease, cancellationToken);
        var run = await runs.LoadAsync(connection, runId, true, cancellationToken);
        if (!run.Snapshot.Nodes.TryGetValue(nodeKey, out var node))
            throw new ArgumentException($"Run '{runId}' has no node '{nodeKey}'.", nameof(nodeKey));
        var reservations = new List<(Guid Id, Guid AgentId, DateTimeOffset Renewed, DateTimeOffset Expires, bool Closing)>();
        await using (var query = new NpgsqlCommand("""
            SELECT id,agent_id,renewed_at,expires_at,release_requested_at IS NOT NULL
            FROM reservations WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                AND released_at IS NULL ORDER BY agent_id FOR UPDATE
            """, connection))
        {
            query.Parameters.AddWithValue(runId);
            query.Parameters.AddWithValue(nodeKey);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                reservations.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetFieldValue<DateTimeOffset>(3), reader.GetBoolean(4)));
        }
        if (reservations.Count == 0 || reservations.Any(row => row.Closing)) return false;
        var now = time.GetUtcNow();
        var expired = reservations.Any(row => row.Expires <= now);
        var ownerEnded = run.Snapshot.Status is RunStatus.Aborting || TransitionTable.IsTerminal(run.Snapshot.Status) ||
            TransitionTable.IsTerminal(node);
        if (forceRenew && (expired || ownerEnded)) return false;
        reason ??= expired ? "Reservation lease expired." : ownerEnded ? $"Reservation owner ended ({run.Snapshot.Status}/{node})." : null;
        if (reason is null)
        {
            foreach (var row in reservations)
                if (forceRenew || now - row.Renewed >= (row.Expires - row.Renewed) / 2)
                    await RunStore.ExecuteAsync(connection, """
                        UPDATE reservations SET renewed_at=$2,expires_at=$2+(expires_at-renewed_at) WHERE id=$1
                        """, cancellationToken, row.Id, now);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        var category = expired ? FailureCategory.Timeout :
            run.Snapshot.Status is RunStatus.Aborting or RunStatus.Aborted ? FailureCategory.Aborted : FailureCategory.Placement;
        await CancelOwnerAsync(connection, run, nodeKey, now, category, reason, cancellationToken);
        foreach (var row in reservations)
            await RunStore.ExecuteAsync(connection, """
                WITH fence AS (
                    UPDATE agents SET reservation_epoch=nextval(pg_get_serial_sequence('reservations','epoch'))
                    WHERE id=$2 RETURNING reservation_epoch
                )
                UPDATE reservations SET release_requested_at=$3,release_requested_reason=$4,fence_epoch=fence.reservation_epoch
                FROM fence WHERE id=$1
                """, cancellationToken, row.Id, row.AgentId, now, reason);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Reservation release requested for run {RunId}, node {NodeKey}: {Reason}",
            runId, nodeKey, reason);
        return false;
    }

    private async Task CancelOwnerAsync(NpgsqlConnection connection, RunState run, string nodeKey, DateTimeOffset now,
        FailureCategory category, string reason, CancellationToken cancellationToken)
    {
        if (!TransitionTable.IsTerminal(run.Snapshot.Status))
        {
            var at = run.Snapshot.Abort?.At is { } abort && abort > now ? abort : now;
            foreach (var task in run.Snapshot.Tasks.Where(task => task.Key.StartsWith(nodeKey + "/", StringComparison.Ordinal) &&
                         !TransitionTable.IsTerminal(task.Value)).OrderBy(task => task.Key, StringComparer.Ordinal))
                await RunStore.AppendAsync(connection, run,
                    run.CreateEvent(EventEntity.Task, task.Key, (int)ExecutionStatus.Cancelled, at) with
                        { FailureCategory = category }, cancellationToken);
            if (!TransitionTable.IsTerminal(run.Snapshot.Nodes[nodeKey]))
                await RunStore.AppendAsync(connection, run,
                    run.CreateEvent(EventEntity.Node, nodeKey, (int)ExecutionStatus.Cancelled, at) with
                        { FailureCategory = category }, cancellationToken);
        }
        else
            logger.LogWarning("Settling reservation attempts after terminal run {RunId}; no late run events will be appended.",
                run.Snapshot.RunId);
        await RunStore.ExecuteAsync(connection, """
            WITH cancelled AS (
                UPDATE task_attempts SET status=$4,ended_at=$3,error=$5,failure_category=$6,
                    queue_wait_ms=GREATEST(0,EXTRACT(EPOCH FROM (COALESCE(started_at,$3)-queued_at))*1000)
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND split_part(task_key,'/',1)=$2
                    AND (status IN (2,3,4) OR (status=$4 AND ended_at=$3))
                RETURNING run_day,run_id,task_key,attempt
            )
            DELETE FROM dispatch_outbox o USING task_attempts t
            WHERE o.run_day=t.run_day AND o.run_id=t.run_id AND o.task_key=t.task_key AND o.attempt=t.attempt
                AND t.run_day=uuid_v7_run_day($1) AND t.run_id=$1 AND split_part(t.task_key,'/',1)=$2
            """, cancellationToken, run.Snapshot.RunId, nodeKey, now, (short)ExecutionStatus.Cancelled, reason, (short)category);
    }

    public async Task<IReadOnlyList<ReservationFence>> PendingAsync(AgentSession session,
        CancellationToken cancellationToken = default)
    {
        var lease = runs.CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await runs.FenceAsync(connection, transaction, lease, cancellationToken);
        await using var query = new NpgsqlCommand("""
            SELECT r.fence_epoch,r.release_requested_reason FROM reservations r JOIN agents a ON a.id=r.agent_id
            WHERE r.agent_id=$1 AND a.session_id=$2 AND r.released_at IS NULL AND r.fence_epoch IS NOT NULL
            ORDER BY r.fence_epoch
            """, connection);
        query.Parameters.AddWithValue(session.AgentId);
        query.Parameters.AddWithValue(session.SessionId);
        var pending = new List<ReservationFence>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            pending.Add(new() { ReservationEpoch = checked((ulong)reader.GetInt64(0)), Reason = reader.GetString(1) });
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return pending;
    }

    public async Task ConfirmAsync(AgentSession session, ulong epoch, CancellationToken cancellationToken = default)
    {
        if (epoch is 0 or > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(epoch));
        var lease = runs.CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, lease, cancellationToken);
        long current, confirmed;
        await using (var query = new NpgsqlCommand("""
            SELECT session_id,reservation_epoch,reservation_fenced_epoch FROM agents WHERE id=$1 FOR UPDATE
            """, connection))
        {
            query.Parameters.AddWithValue(session.AgentId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != session.SessionId)
                throw new ArgumentException("Reservation fence acknowledgement was superseded by a new Hello.", nameof(session));
            current = reader.GetInt64(1);
            confirmed = reader.GetInt64(2);
        }
        if ((long)epoch <= confirmed) return;
        if ((long)epoch != current) throw new ArgumentException("Reservation fence acknowledgement does not match the current epoch.", nameof(epoch));
        await using (var release = new NpgsqlCommand("""
            UPDATE reservations SET released_at=$3,release_reason=release_requested_reason
            WHERE agent_id=$1 AND fence_epoch=$2 AND released_at IS NULL AND release_requested_at IS NOT NULL
            RETURNING id
            """, connection))
        {
            release.Parameters.AddWithValue(session.AgentId);
            release.Parameters.AddWithValue((long)epoch);
            release.Parameters.AddWithValue(time.GetUtcNow());
            if (await release.ExecuteScalarAsync(cancellationToken) is null)
                throw new ArgumentException("No reservation release requested this fence acknowledgement.", nameof(epoch));
        }
        await RunStore.ExecuteAsync(connection, "UPDATE agents SET reservation_fenced_epoch=$2 WHERE id=$1",
            cancellationToken, session.AgentId, (long)epoch);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Physically fenced reservation epoch {Epoch} acknowledged by agent {AgentId}.", epoch, session.AgentId);
    }

    private async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LeaseToken? lease,
        CancellationToken cancellationToken)
    {
        await runs.FenceAsync(connection, transaction, lease, cancellationToken);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, ReservationManager.AllocationLock);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SweepAsync(stoppingToken); }
                catch (NotLeaderException) { logger.LogDebug("Reservation maintenance paused because this engine is not leader."); }
                catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
                {
                    logger.LogError(exception, "Reservation maintenance will retry when durable storage is available.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
