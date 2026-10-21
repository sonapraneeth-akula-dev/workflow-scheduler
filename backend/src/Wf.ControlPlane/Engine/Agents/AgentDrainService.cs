using System.Text;
using Google.Protobuf.WellKnownTypes;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;

namespace Wf.ControlPlane.Engine.Agents;

public sealed record AgentDrainRecord(Guid Id, Guid AgentId, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset DeadlineAt, string Reason, DateTimeOffset? ReleasedAt);

public sealed class AgentDrainService(NpgsqlDataSource source, RunStore runs, AgentRegistry registry, TimeProvider time)
{
    public async Task<AgentDrainRecord> RequestAsync(Guid machine, int maxDrainSeconds, string reason,
        string actor, string actorKind, CancellationToken token, bool requireResumable = false)
    {
        if (machine == Guid.Empty || maxDrainSeconds is < 1 or > 86400 ||
            string.IsNullOrWhiteSpace(reason) || Encoding.UTF8.GetByteCount(reason) > 1024 ||
            !Guid.TryParseExact(actor, "D", out var principal) || principal == Guid.Empty || actorKind is not ("User" or "App"))
            throw new ArgumentException("Drain requires a machine, 1..86400 seconds, bounded reason and audit principal.");
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        short status;
        bool supported;
        bool processIdentified;
        await using (var query = new NpgsqlCommand("""
            SELECT status,supports_drain_commands,process_instance_id IS NOT NULL FROM agents WHERE id=$1 FOR UPDATE
            """, connection))
        {
            query.Parameters.AddWithValue(machine);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Machine not found.");
            status = reader.GetInt16(0);
            supported = reader.GetBoolean(1);
            processIdentified = reader.GetBoolean(2);
        }
        if (requireResumable && !processIdentified)
            throw new InvalidOperationException("A resumable drain requires an agent process identity before maintenance.");
        var existing = await ReadAsync(connection, machine, token);
        if (existing is not null) return existing;
        if (status == 2 || !supported)
            throw new InvalidOperationException("Machine is already draining or does not support drain commands.");
        var ticks = time.GetUtcNow().UtcTicks;
        var now = new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
        var record = new AgentDrainRecord(Guid.CreateVersion7(now), machine, "Draining", now,
            now.AddSeconds(maxDrainSeconds), reason, null);
        await RunStore.ExecuteAsync(connection, """
            INSERT INTO drain_requests(id,agent_id,created_at,deadline_at,reason,requested_actor,requested_actor_kind)
            VALUES($1,$2,$3,$4,$5,$6,$7)
            """, token, record.Id, machine, now, record.DeadlineAt, reason, actor, actorKind);
        await RunStore.ExecuteAsync(connection, "UPDATE agents SET status=2 WHERE id=$1", token, machine);
        await AuditAsync(connection, actor, actorKind, "machine.drain", record, token);
        await transaction.CommitAsync(token);
        await registry.LoadAsync(token);
        registry.NotifyDispatch(machine);
        return record;
    }

    public async Task<AgentDrainRecord?> FindAsync(Guid machine, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return await ReadAsync(connection, machine, token);
    }

    private static async Task<AgentDrainRecord?> ReadAsync(NpgsqlConnection connection, Guid machine, CancellationToken token)
    {
        await using var query = new NpgsqlCommand("""
            SELECT id,created_at,deadline_at,reason,released_at FROM drain_requests WHERE agent_id=$1
            """, connection);
        query.Parameters.AddWithValue(machine);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetGuid(0), machine, reader.IsDBNull(4) ? "Draining" : "Released",
            reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task<Drain?> PendingAsync(AgentSession session, CancellationToken token)
    {
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        Drain? command = null;
        await using (var query = new NpgsqlCommand("""
            SELECT d.id,d.deadline_at,d.reason,a.reservation_epoch FROM drain_requests d JOIN agents a ON a.id=d.agent_id
            WHERE d.agent_id=$1 AND a.session_id=$2 AND a.status=2 AND a.supports_drain_commands AND d.released_at IS NULL
            """, connection))
        {
            query.Parameters.AddWithValue(session.AgentId);
            query.Parameters.AddWithValue(session.SessionId);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                command = new() { Id = reader.GetGuid(0).ToString("D"),
                    DeadlineAt = Timestamp.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(1)),
                    Reason = reader.GetString(2), ReservationEpoch = checked((ulong)reader.GetInt64(3)) };
        }
        await transaction.CommitAsync(token);
        return command;
    }

    public async Task AcceptAsync(AgentSession session, DrainCompleted result, CancellationToken token)
    {
        var id = DrainProtocol.Validate(result);
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        string actor, actorKind;
        await using (var query = new NpgsqlCommand("""
            SELECT d.requested_actor,d.requested_actor_kind FROM drain_requests d JOIN agents a ON a.id=d.agent_id
            WHERE d.id=$1 AND d.agent_id=$2 AND d.released_at IS NULL AND a.session_id=$3
                AND a.status=2 AND a.reservation_epoch=$4 FOR UPDATE OF a,d
            """, connection))
        {
            query.Parameters.AddWithValue(id);
            query.Parameters.AddWithValue(session.AgentId);
            query.Parameters.AddWithValue(session.SessionId);
            query.Parameters.AddWithValue((long)result.ReservationEpoch);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return;
            actor = reader.GetString(0);
            actorKind = reader.GetString(1);
        }
        var now = time.GetUtcNow();
        // The replacement sweeper applies the original retry/placement policy. No synthetic task success.
        await RunStore.ExecuteAsync(connection, """
            WITH lost AS (
                UPDATE task_attempts SET status=7,failure_category=2,error='Agent drained for machine maintenance.',
                    ended_at=$2,queue_wait_ms=GREATEST(0,EXTRACT(EPOCH FROM (COALESCE(started_at,$2)-queued_at))*1000)
                WHERE agent_id=$1 AND status IN (2,3) RETURNING run_day,run_id,task_key,attempt
            )
            DELETE FROM dispatch_outbox o USING lost l
            WHERE o.run_day=l.run_day AND o.run_id=l.run_id AND o.task_key=l.task_key AND o.attempt=l.attempt
            """, token, session.AgentId, now);
        await RunStore.ExecuteAsync(connection, """
            UPDATE reservations SET released_at=$2,release_reason='Agent physically drained for maintenance.'
            WHERE agent_id=$1 AND released_at IS NULL
            """, token, session.AgentId, now);
        await RunStore.ExecuteAsync(connection, """
            UPDATE agents SET slots_used=0,reservation_fenced_epoch=reservation_epoch WHERE id=$1
            """, token, session.AgentId);
        await RunStore.ExecuteAsync(connection, """
            UPDATE drain_requests d SET released_at=$2,released_process_instance_id=a.process_instance_id
            FROM agents a WHERE d.id=$1 AND a.id=d.agent_id
            """, token, id, now);
        var record = await ReadAsync(connection, session.AgentId, token)
            ?? throw new InvalidDataException("Committed drain disappeared.");
        await AuditAsync(connection, actor, actorKind, "machine.drain.completed", record, token);
        await transaction.CommitAsync(token);
        await registry.LoadAsync(token);
    }

    public async Task ResumeAsync(Guid machine, Guid drainId, string reason, string actor, string actorKind,
        CancellationToken token)
    {
        if (machine == Guid.Empty || drainId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reason) || Encoding.UTF8.GetByteCount(reason) > 1024 ||
            !Guid.TryParseExact(actor, "D", out var principal) || principal == Guid.Empty || actorKind is not ("User" or "App"))
            throw new ArgumentException("Resume requires a machine, drain identity, bounded reason and audit principal.");
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        await using (var query = new NpgsqlCommand("""
            SELECT a.status,a.slots_used,a.last_heartbeat_at,a.process_instance_id,
                d.id,d.released_at,d.released_process_instance_id,
                NOT EXISTS(SELECT 1 FROM reservations r WHERE r.agent_id=a.id AND r.released_at IS NULL),
                NOT EXISTS(SELECT 1 FROM task_attempts t WHERE t.agent_id=a.id AND t.status IN (2,3))
            FROM agents a LEFT JOIN drain_requests d ON d.agent_id=a.id WHERE a.id=$1 FOR UPDATE OF a
            """, connection))
        {
            query.Parameters.AddWithValue(machine);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Machine not found.");
            if (reader.IsDBNull(4) && reader.GetInt16(0) == 0) return;
            if (reader.GetInt16(0) != 2 || reader.GetInt32(1) != 0 ||
                reader.GetFieldValue<DateTimeOffset>(2) <= time.GetUtcNow() - AgentRegistry.HeartbeatTimeout ||
                reader.IsDBNull(3) || reader.IsDBNull(4) || reader.GetGuid(4) != drainId ||
                reader.IsDBNull(5) || reader.IsDBNull(6) || reader.GetGuid(3) == reader.GetGuid(6) ||
                !reader.GetBoolean(7) || !reader.GetBoolean(8))
                throw new InvalidOperationException("Resume requires physical release and a fresh, idle, restarted agent process.");
        }
        await RunStore.ExecuteAsync(connection, "DELETE FROM drain_requests WHERE agent_id=$1 AND id=$2", token, machine, drainId);
        await RunStore.ExecuteAsync(connection, "UPDATE agents SET status=0 WHERE id=$1", token, machine);
        await RunStore.ExecuteAsync(connection, """
            INSERT INTO audit_log(actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,$2,'machine.resume','Machine',$3,jsonb_build_object('drainId',$4::text,'reason',$5::text,
                'before',jsonb_build_object('status','Draining'),'after',jsonb_build_object('status','Healthy')))
            """, token, actor, actorKind, machine.ToString("D"), drainId.ToString("D"), reason);
        await transaction.CommitAsync(token);
        await registry.LoadAsync(token);
        registry.NotifyDispatch(machine);
    }

    private static Task AuditAsync(NpgsqlConnection connection, string actor, string kind, string action,
        AgentDrainRecord record, CancellationToken token) => RunStore.ExecuteAsync(connection, """
            INSERT INTO audit_log(actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,$2,$3,'Machine',$4,jsonb_build_object('drainId',$5::text,'deadlineAt',$6::timestamptz,'reason',$7::text))
            """, token, actor, kind, action, record.AgentId.ToString("D"), record.Id.ToString("D"), record.DeadlineAt, record.Reason);
}
