using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core.Placement;

namespace Wf.ControlPlane.Engine.Reservations;

public sealed record MachineReservation(Guid Id, Guid RunId, string NodeKey, Guid AgentId, long Epoch,
    DateTimeOffset CreatedAt, DateTimeOffset RenewedAt, DateTimeOffset ExpiresAt);

public sealed class ReservationUnavailableException(Guid runId, string nodeKey, int count)
    : InvalidOperationException($"ReservationUnavailable: node '{nodeKey}' in run '{runId:D}' requires {count} available machines.");

public sealed class ReservationManager(NpgsqlDataSource dataSource, RunStore runs, TimeProvider timeProvider)
{
    internal const long AllocationLock = 746312081;

    public async Task<IReadOnlyList<MachineReservation>> ReserveAsync(Guid runId, string nodeKey, int count,
        PlacementSelector selector, TimeSpan lifetime, CancellationToken cancellationToken = default)
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
        var json = JsonSerializer.Serialize(canonical, ReservationJson.Default.PlacementSelector);
        var lease = runs.CaptureLease();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await runs.FenceAsync(connection, transaction, lease, cancellationToken);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, AllocationLock);
        await using (var owner = new NpgsqlCommand("""
            SELECT 1 FROM runs r JOIN node_runs n ON n.run_day=r.run_day AND n.run_id=r.id
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1 AND n.node_key=$2
                AND r.status IN (0,1) AND n.status BETWEEN 0 AND 4 FOR UPDATE OF r,n
            """, connection, transaction))
        {
            owner.Parameters.AddWithValue(runId);
            owner.Parameters.AddWithValue(nodeKey);
            if (await owner.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("Only an existing nonterminal run and node can reserve machines.");
        }
        var now = timeProvider.GetUtcNow();
        var existing = new List<MachineReservation>();
        await using (var retry = new NpgsqlCommand("""
            SELECT id,run_id,node_key,agent_id,epoch,created_at,renewed_at,expires_at,selector=$3::jsonb
            FROM reservations WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                AND released_at IS NULL ORDER BY agent_id FOR UPDATE
            """, connection, transaction))
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
                if (reservation.ExpiresAt <= now) throw new ReservationUnavailableException(runId, nodeKey, count);
                existing.Add(reservation);
            }
        }
        if (existing.Count > 0)
        {
            if (existing.Count != count)
                throw new InvalidOperationException("An active node reservation cannot change its machine count.");
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        var candidates = new List<PlacementAgent>();
        await using (var query = new NpgsqlCommand("""
            SELECT a.id,a.name,a.os,a.pool,a.max_tasks,a.ram_mb,a.labels FROM agents a
            WHERE a.status=0 AND a.last_heartbeat_at>$1 AND a.slots_used=0
                AND NOT EXISTS(SELECT 1 FROM reservations r WHERE r.agent_id=a.id AND r.released_at IS NULL)
                AND NOT EXISTS(SELECT 1 FROM task_attempts t WHERE t.agent_id=a.id AND t.status IN (2,3,4))
            ORDER BY a.id FOR UPDATE OF a
            """, connection, transaction))
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
        var selected = candidates.OrderBy(agent => agent.Id).Take(count).ToArray();
        if (selected.Length != count) throw new ReservationUnavailableException(runId, nodeKey, count);
        foreach (var agent in selected)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO reservations(id,run_day,run_id,node_key,agent_id,selector,created_at,renewed_at,expires_at)
                VALUES($1,uuid_v7_run_day($2),$2,$3,$4,$5::jsonb,$6,$6,$7)
                RETURNING id,run_id,node_key,agent_id,epoch,created_at,renewed_at,expires_at
                """, connection, transaction);
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
            existing.Add(Read(reader));
        }
        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    private static MachineReservation Read(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt64(4),
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7));
}

[JsonSerializable(typeof(PlacementSelector))]
internal sealed partial class ReservationJson : JsonSerializerContext;
