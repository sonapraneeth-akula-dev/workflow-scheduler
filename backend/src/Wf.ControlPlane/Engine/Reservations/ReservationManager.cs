using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core.Placement;

namespace Wf.ControlPlane.Engine.Reservations;

public sealed record MachineReservation(Guid Id, Guid RunId, string NodeKey, Guid AgentId, long Epoch,
    DateTimeOffset CreatedAt, DateTimeOffset RenewedAt, DateTimeOffset ExpiresAt);

public sealed class ReservationUnavailableException(Guid runId, string nodeKey, int count)
    : InvalidOperationException($"ReservationUnavailable: node '{nodeKey}' in run '{runId:D}' requires {count} available machines.");

public sealed record ReservationRequestResult(ReservationDisposition Disposition,
    IReadOnlyList<MachineReservation> Reservations, int? Position = null, string? Reason = null);

public sealed class ReservationManager(NpgsqlDataSource dataSource, RunStore runs, TimeProvider timeProvider)
{
    internal const long AllocationLock = 746312081;

    public async Task<IReadOnlyList<MachineReservation>> ReserveAsync(Guid runId, string nodeKey, int count,
        PlacementSelector selector, TimeSpan lifetime, CancellationToken cancellationToken = default)
        => (await ExecuteAsync(runId, nodeKey, count, selector, lifetime, null, cancellationToken)).Reservations;

    public Task<ReservationRequestResult> RequestAsync(Guid runId, string nodeKey, int count,
        PlacementSelector selector, TimeSpan lifetime, WhenUnavailable whenUnavailable = WhenUnavailable.Queue,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(whenUnavailable)) throw new ArgumentOutOfRangeException(nameof(whenUnavailable));
        return ExecuteAsync(runId, nodeKey, count, selector, lifetime, whenUnavailable, cancellationToken);
    }

    private async Task<ReservationRequestResult> ExecuteAsync(Guid runId, string nodeKey, int count,
        PlacementSelector selector, TimeSpan lifetime, WhenUnavailable? policy, CancellationToken cancellationToken)
    {
        var json = Canonical(runId, nodeKey, count, selector, lifetime);
        var lease = runs.CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await runs.FenceAsync(connection, transaction, lease, cancellationToken);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, AllocationLock);
        await PruneAsync(connection, cancellationToken);
        var result = await RequestCoreAsync(connection, runId, nodeKey, count, selector, json, lifetime, policy, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private string Canonical(Guid runId, string nodeKey, int count, PlacementSelector selector, TimeSpan lifetime)
    {
        if (runId.Version != 7 || string.IsNullOrWhiteSpace(nodeKey) || nodeKey.Contains('/'))
            throw new ArgumentException("Reservation requires a UUIDv7 run and a node key.");
        ArgumentNullException.ThrowIfNull(selector);
        PlacementFilter.EnsureResolved(selector);
        if (count <= 0 || selector.Count is { } expected && expected != count ||
            !selector.MachineIds.IsEmpty && count > selector.MachineIds.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Reservation count must be positive and consistent with its selector.");
        if (lifetime < TimeSpan.FromMicroseconds(1) || lifetime > DateTimeOffset.MaxValue - timeProvider.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var canonical = new PlacementSelector(selector.Os, selector.Pool, selector.Labels,
            selector.Machines.OrderBy(machine => machine.Literal, StringComparer.Ordinal).ToImmutableArray(),
            selector.MachineIds.Order().ToImmutableArray(), count);
        return JsonSerializer.Serialize(canonical, ReservationJson.Default.PlacementSelector);
    }

    internal Task<ReservationRequestResult> RequestLockedAsync(NpgsqlConnection connection, Guid runId,
        string nodeKey, int count, PlacementSelector selector, TimeSpan lifetime, WhenUnavailable policy,
        CancellationToken cancellationToken) => RequestCoreAsync(connection, runId, nodeKey, count, selector,
            Canonical(runId, nodeKey, count, selector, lifetime), lifetime, policy, cancellationToken);

    private async Task<ReservationRequestResult> RequestCoreAsync(NpgsqlConnection connection, Guid runId,
        string nodeKey, int count, PlacementSelector selector, string json, TimeSpan lifetime, WhenUnavailable? policy,
        CancellationToken cancellationToken)
    {
        RunPlacement? singleMachine = null;
        await using (var owner = new NpgsqlCommand("""
            SELECT r.placement FROM runs r JOIN node_runs n ON n.run_day=r.run_day AND n.run_id=r.id
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1 AND n.node_key=$2
                AND r.status IN (0,1) AND n.status BETWEEN 0 AND 4 FOR UPDATE OF r,n
            """, connection))
        {
            owner.Parameters.AddWithValue(runId);
            owner.Parameters.AddWithValue(nodeKey);
            var persistedPlacement = await owner.ExecuteScalarAsync(cancellationToken);
            if (persistedPlacement is null)
                throw new InvalidOperationException("Only an existing nonterminal run and node can reserve machines.");
            if (persistedPlacement is string jsonPlacement)
                singleMachine = JsonSerializer.Deserialize(jsonPlacement, RunJsonContext.Default.RunPlacement)
                    ?? throw new InvalidDataException("A persisted placement override is null.");
            else if (persistedPlacement is not DBNull)
                throw new InvalidDataException("A persisted placement override is invalid.");
        }
        var now = timeProvider.GetUtcNow();
        var existing = new List<MachineReservation>();
        await using (var retry = new NpgsqlCommand("""
            SELECT id,run_id,node_key,agent_id,epoch,created_at,renewed_at,expires_at,selector=$3::jsonb,
                release_requested_at IS NULL
            FROM reservations WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                AND released_at IS NULL ORDER BY agent_id FOR UPDATE
            """, connection))
        {
            retry.Parameters.AddWithValue(runId);
            retry.Parameters.AddWithValue(nodeKey);
            retry.Parameters.AddWithValue(json);
            await using var reader = await retry.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(8))
                    throw new InvalidOperationException("An active node reservation cannot change its selector.");
                var reservation = Read(reader);
                if (reservation.ExpiresAt <= now || !reader.GetBoolean(9))
                    throw new ReservationUnavailableException(runId, nodeKey, count);
                existing.Add(reservation);
            }
        }
        if (existing.Count > 0)
        {
            if (existing.Count != count)
                throw new InvalidOperationException("An active node reservation cannot change its machine count.");
            return new(ReservationDisposition.Reserved, existing);
        }
        long? queueId = null;
        await using (var queued = new NpgsqlCommand("""
            SELECT position,selector=$3::jsonb AND machine_count=$4 AND lifetime=$5
            FROM reservation_queue WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
            """, connection))
        {
            queued.Parameters.AddWithValue(runId);
            queued.Parameters.AddWithValue(nodeKey);
            queued.Parameters.AddWithValue(json);
            queued.Parameters.AddWithValue(count);
            queued.Parameters.AddWithValue(lifetime);
            await using var reader = await queued.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(1) || policy == WhenUnavailable.Reject)
                    throw new InvalidOperationException("A queued node reservation cannot change its selector, count, lifetime or policy.");
                queueId = reader.GetInt64(0);
            }
        }
        int position;
        await using (var ahead = new NpgsqlCommand(
            "SELECT count(*) FROM reservation_queue WHERE position<COALESCE($1,9223372036854775807)", connection))
        {
            ahead.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = (object?)queueId ?? DBNull.Value });
            position = checked((int)(long)(await ahead.ExecuteScalarAsync(cancellationToken))! + 1);
        }
        var candidates = new List<PlacementAgent>();
        if (position == 1)
        await using (var query = new NpgsqlCommand("""
            SELECT a.id,a.name,a.os,a.pool,a.max_tasks,a.ram_mb,a.labels FROM agents a
            WHERE a.status=0 AND a.last_heartbeat_at>$1 AND a.slots_used=0
                AND NOT EXISTS(SELECT 1 FROM reservations r WHERE r.agent_id=a.id AND r.released_at IS NULL)
                AND NOT EXISTS(SELECT 1 FROM task_attempts t WHERE t.agent_id=a.id AND t.status IN (2,3,4))
            ORDER BY a.id FOR UPDATE OF a
            """, connection))
        {
            query.Parameters.AddWithValue(now - AgentRegistry.HeartbeatTimeout);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var labels = reader.GetFieldValue<string[]>(6).Select(label => label.Split('=', 2))
                    .ToImmutableDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
                var agent = new PlacementAgent(reader.GetGuid(0), reader.GetString(1),
                    reader.GetInt16(2) == 0 ? "windows" : "linux", reader.GetString(3),
                    0, reader.GetInt32(4), reader.GetInt32(5), Labels: labels);
                if (PlacementFilter.Matches(selector, agent)) candidates.Add(agent);
            }
        }
        if (singleMachine?.SingleMachine == true)
        {
            var run = await runs.LoadAsync(connection, runId, true, cancellationToken);
            var nodes = await RunExecutionService.PlacementNodesAsync(connection, run, cancellationToken);
            candidates.RemoveAll(agent => run.Snapshot.PlacementAgentId is { } pin && agent.Id != pin ||
                !PlacementOverrideValidator.Matches(agent, singleMachine, nodes));
        }
        var selected = candidates.OrderBy(agent => agent.Id).Take(count).ToArray();
        var disposition = ReservationQueue.Decide(policy ?? WhenUnavailable.Reject, position, selected.Length == count);
        if (disposition != ReservationDisposition.Reserved)
        {
            if (policy is null) throw new ReservationUnavailableException(runId, nodeKey, count);
            if (disposition == ReservationDisposition.Rejected)
            {
                var run = await runs.LoadAsync(connection, runId, true, cancellationToken);
                foreach (var change in ReservationQueue.Reject(run, nodeKey, now))
                    await RunStore.AppendAsync(connection, run, change, cancellationToken);
                await RunStore.ExecuteAsync(connection, """
                    WITH cancelled AS (
                        UPDATE task_attempts SET status=$5,ended_at=$2,error=$3,failure_category=$4
                        WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                            AND (status IN (2,3,4) OR (status=$5 AND ended_at=$2)) RETURNING run_id
                    ), dispatches AS (
                        DELETE FROM dispatch_outbox WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 RETURNING run_id
                    )
                    DELETE FROM reservation_queue WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                    """, cancellationToken, runId, now, ReservationQueue.UnavailableReason,
                    (short)FailureCategory.Placement, (short)ExecutionStatus.Cancelled);
                return new(disposition, [], Reason: ReservationQueue.UnavailableReason);
            }
            if (queueId is null)
                await RunStore.ExecuteAsync(connection, """
                    INSERT INTO reservation_queue(run_day,run_id,node_key,machine_count,selector,lifetime,queued_at)
                    VALUES(uuid_v7_run_day($1),$1,$2,$3,$4::jsonb,$5,$6)
                    """, cancellationToken, runId, nodeKey, count, json, lifetime, now);
            return new(disposition, [], position);
        }
        foreach (var agent in selected)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO reservations(id,run_day,run_id,node_key,agent_id,selector,created_at,renewed_at,expires_at)
                VALUES($1,uuid_v7_run_day($2),$2,$3,$4,$5::jsonb,$6,$6,$7)
                RETURNING id,run_id,node_key,agent_id,epoch,created_at,renewed_at,expires_at
                """, connection);
            insert.Parameters.AddWithValue(Guid.CreateVersion7(now));
            insert.Parameters.AddWithValue(runId);
            insert.Parameters.AddWithValue(nodeKey);
            insert.Parameters.AddWithValue(agent.Id);
            insert.Parameters.AddWithValue(json);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(now + lifetime);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidDataException("Reservation insert returned no identity.");
            var reservation = Read(reader);
            existing.Add(reservation);
            await reader.DisposeAsync();
            await RunStore.ExecuteAsync(connection, "UPDATE agents SET reservation_epoch=$2 WHERE id=$1",
                cancellationToken, agent.Id, reservation.Epoch);
        }
        await RunStore.ExecuteAsync(connection,
            "DELETE FROM reservation_queue WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2",
            cancellationToken, runId, nodeKey);
        return new(ReservationDisposition.Reserved, existing);
    }

    public async Task SweepQueueAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var lease = runs.CaptureLease();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await runs.FenceAsync(connection, transaction, lease, cancellationToken);
            await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, AllocationLock);
            await PruneAsync(connection, cancellationToken);
            (Guid Run, string Node, int Count, string Json, TimeSpan Lifetime)? head = null;
            await using (var query = new NpgsqlCommand("""
                SELECT q.run_id,q.node_key,q.machine_count,q.selector::text,q.lifetime FROM reservation_queue q
                JOIN runs r ON r.run_day=q.run_day AND r.id=q.run_id
                JOIN node_runs n ON n.run_day=q.run_day AND n.run_id=q.run_id AND n.node_key=q.node_key
                WHERE r.status IN (0,1) AND n.status BETWEEN 0 AND 4 ORDER BY q.position LIMIT 1 FOR UPDATE OF r,n
                """, connection))
            {
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                    head = (reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetTimeSpan(4));
            }
            if (head is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            var selector = JsonSerializer.Deserialize(head.Value.Json, ReservationJson.Default.PlacementSelector)
                ?? throw new InvalidDataException("Queued reservation selector is null.");
            var result = await RequestCoreAsync(connection, head.Value.Run, head.Value.Node, head.Value.Count,
                selector, head.Value.Json, head.Value.Lifetime, WhenUnavailable.Queue, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (result.Disposition == ReservationDisposition.Queued) return;
        }
    }

    private static Task PruneAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
        RunStore.ExecuteAsync(connection, """
            DELETE FROM reservation_queue q USING runs r,node_runs n
            WHERE r.run_day=q.run_day AND r.id=q.run_id AND n.run_day=q.run_day AND n.run_id=q.run_id
                AND n.node_key=q.node_key AND (r.status NOT IN (0,1) OR n.status NOT BETWEEN 0 AND 4)
            """, cancellationToken);

    private static MachineReservation Read(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt64(4),
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7));
}

[JsonSerializable(typeof(PlacementSelector))]
internal sealed partial class ReservationJson : JsonSerializerContext;
