using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;

namespace Wf.ControlPlane.Engine.Agents;

public sealed record CalibrationRecord(Guid Id, Guid AgentId, CapacityProfile Profile, long Epoch, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset DeadlineAt, DateTimeOffset? DispatchedAt, DateTimeOffset? FinishedAt,
    int? CapacityRps, bool Applied, string? Error);

public sealed class CalibrationService(NpgsqlDataSource source, RunStore runs, AgentRegistry registry, TimeProvider time)
{
    public async Task<CalibrationRecord> RequestAsync(Guid machine, CapacityProfile profile, string actor, string actorKind,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (machine == Guid.Empty || !Guid.TryParseExact(actor, "D", out var principal) || principal == Guid.Empty ||
            actorKind is not ("User" or "App"))
            throw new ArgumentException("Calibration requires a machine and audit principal.");
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        var now = time.GetUtcNow();
        await using (var query = new NpgsqlCommand("""
            SELECT a.status,a.last_heartbeat_at,a.slots_used,a.supports_calibration_commands,
                EXISTS(SELECT 1 FROM reservations r WHERE r.agent_id=a.id AND r.released_at IS NULL),
                EXISTS(SELECT 1 FROM task_attempts t WHERE t.agent_id=a.id AND t.status IN (2,3,4)),
                EXISTS(SELECT 1 FROM calibration_requests c WHERE c.agent_id=a.id AND c.status IN (0,1))
            FROM agents a WHERE a.id=$1 FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(machine);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Machine not found.");
            if (reader.GetInt16(0) != 0 || reader.GetFieldValue<DateTimeOffset>(1) <= now - AgentRegistry.HeartbeatTimeout ||
                reader.GetInt32(2) != 0 || !reader.GetBoolean(3) || reader.GetBoolean(4) || reader.GetBoolean(5) || reader.GetBoolean(6))
                throw new InvalidOperationException("Calibration requires a fresh, idle, unreserved machine supporting calibration commands.");
        }
        long epoch;
        await using (var update = new NpgsqlCommand("""
            UPDATE agents SET status=2,reservation_epoch=nextval(pg_get_serial_sequence('reservations','epoch'))
            WHERE id=$1 RETURNING reservation_epoch
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(machine);
            epoch = (long)(await update.ExecuteScalarAsync(token) ?? throw new InvalidDataException("Calibration machine disappeared."));
        }
        var id = Guid.CreateVersion7(now);
        var deadline = now + TimeSpan.FromMinutes(2);
        await RunStore.ExecuteAsync(connection, """
            INSERT INTO calibration_requests(id,agent_id,profile_key,epoch,requested_actor,requested_actor_kind,created_at,deadline_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8)
            """, token, id, machine, profile.ProfileKey, epoch, actor, actorKind, now, deadline);
        await AuditAsync(connection, actor, actorKind, "machine.calibrate", machine, id, profile.ProfileKey, null, false, null, token);
        await transaction.CommitAsync(token);
        return new(id, machine, profile, epoch, "Queued", now, deadline, null, null, null, false, null);
    }

    public async Task<CalibrationRecord?> FindAsync(Guid machine, Guid id, CancellationToken token)
    {
        await using var query = source.CreateCommand("""
            SELECT id,agent_id,profile_key,epoch,status,created_at,deadline_at,dispatched_at,finished_at,reported_rps,applied,error
            FROM calibration_requests WHERE agent_id=$1 AND id=$2
            """);
        query.Parameters.AddWithValue(machine);
        query.Parameters.AddWithValue(id);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var key = reader.GetString(2);
        var profile = JsonSerializer.Deserialize(key, CapacityProfileJsonContext.Default.CapacityProfile)
            ?? throw new InvalidDataException("Calibration profile is missing.");
        if (profile.ProfileKey != key) throw new InvalidDataException("Calibration profile is not canonical.");
        return new(reader.GetGuid(0), reader.GetGuid(1), profile, reader.GetInt64(3),
            reader.GetInt16(4) switch { 0 => "Queued", 1 => "Dispatched", 2 => "Succeeded", 3 => "Failed", _ => throw new InvalidDataException() },
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.GetBoolean(10),
            reader.IsDBNull(11) ? null : ((CalibrationOutcome)reader.GetInt16(11)).ToString());
    }

    public async Task<IReadOnlyList<Calibrate>> PendingAsync(AgentSession session, CancellationToken token)
    {
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        await using var query = new NpgsqlCommand("""
            WITH dispatched AS (
                UPDATE calibration_requests c SET status=1,dispatched_at=$3
                FROM agents a WHERE c.agent_id=$1 AND a.id=c.agent_id AND a.session_id=$2
                    AND a.supports_calibration_commands AND a.reservation_epoch=c.epoch AND c.status=0
                RETURNING c.id,c.profile_key,c.deadline_at,c.epoch
            )
            SELECT id,profile_key,deadline_at,epoch FROM dispatched
            UNION ALL
            SELECT c.id,c.profile_key,c.deadline_at,c.epoch FROM calibration_requests c JOIN agents a ON a.id=c.agent_id
            WHERE c.agent_id=$1 AND a.session_id=$2 AND a.supports_calibration_commands
                AND a.reservation_epoch=c.epoch AND c.status=1
            """, connection, transaction);
        query.Parameters.AddWithValue(session.AgentId);
        query.Parameters.AddWithValue(session.SessionId);
        query.Parameters.AddWithValue(time.GetUtcNow());
        var commands = new List<Calibrate>();
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                commands.Add(new()
                {
                    Id = reader.GetGuid(0).ToString("D"), ProfileJson = reader.GetString(1),
                    DeadlineAt = Timestamp.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(2)),
                    ReservationEpoch = checked((ulong)reader.GetInt64(3))
                });
        await transaction.CommitAsync(token);
        return commands;
    }

    public async Task AcceptAsync(AgentSession session, CalibrationCompleted result, CancellationToken token)
    {
        var id = CalibrationProtocol.Validate(result);
        var lease = runs.CaptureLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await runs.FenceAsync(connection, transaction, lease, token);
        await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock($1)", token, ReservationManager.AllocationLock);
        string profile, actor, actorKind;
        DateTimeOffset deadline;
        await using (var query = new NpgsqlCommand("""
            SELECT c.profile_key,c.requested_actor,c.requested_actor_kind,c.deadline_at
            FROM calibration_requests c JOIN agents a ON a.id=c.agent_id
            WHERE c.id=$1 AND c.agent_id=$2 AND a.session_id=$3 AND c.epoch=$4
                AND a.reservation_epoch=c.epoch AND a.status=2 AND c.status IN (0,1)
            FOR UPDATE OF a,c
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(id);
            query.Parameters.AddWithValue(session.AgentId);
            query.Parameters.AddWithValue(session.SessionId);
            query.Parameters.AddWithValue((long)result.ReservationEpoch);
            await using var reader = await query.ExecuteReaderAsync(token);
            // Stale/duplicate results are transport-acknowledged without changing capacity or releasing a machine.
            if (!await reader.ReadAsync(token)) return;
            profile = reader.GetString(0);
            actor = reader.GetString(1);
            actorKind = reader.GetString(2);
            deadline = reader.GetFieldValue<DateTimeOffset>(3);
        }
        var now = time.GetUtcNow();
        var outcome = now >= deadline || result.At.ToDateTimeOffset() >= deadline ? CalibrationOutcome.TimedOut : result.Outcome;
        var succeeded = outcome == CalibrationOutcome.Succeeded;
        var applied = false;
        if (succeeded)
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO agent_capacities(agent_id,profile_key,capacity_rps,source,calibrated_at)
                VALUES($1,$2,$3,1,$4)
                ON CONFLICT(agent_id,profile_key) DO UPDATE SET
                    capacity_rps=EXCLUDED.capacity_rps,source=1,calibrated_at=EXCLUDED.calibrated_at
                WHERE agent_capacities.source<>0
                """, connection, transaction);
            upsert.Parameters.AddWithValue(session.AgentId);
            upsert.Parameters.AddWithValue(profile);
            upsert.Parameters.AddWithValue(result.CapacityRps);
            upsert.Parameters.AddWithValue(now);
            applied = await upsert.ExecuteNonQueryAsync(token) == 1;
        }
        await using (var finish = new NpgsqlCommand("""
            UPDATE calibration_requests SET status=$2,finished_at=$3,reported_rps=$4,applied=$5,error=$6 WHERE id=$1
            """, connection, transaction))
        {
            finish.Parameters.AddWithValue(id);
            finish.Parameters.AddWithValue((short)(succeeded ? 2 : 3));
            finish.Parameters.AddWithValue(now);
            finish.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, succeeded ? result.CapacityRps : DBNull.Value);
            finish.Parameters.AddWithValue(applied);
            finish.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Smallint, succeeded ? DBNull.Value : (short)outcome);
            await finish.ExecuteNonQueryAsync(token);
        }
        await RunStore.ExecuteAsync(connection, """
            UPDATE agents SET status=CASE WHEN last_heartbeat_at>$2 THEN 0 ELSE 1 END WHERE id=$1
            """, token, session.AgentId, now - AgentRegistry.HeartbeatTimeout);
        await AuditAsync(connection, actor, actorKind, "machine.calibration.completed", session.AgentId, id, profile,
            succeeded ? result.CapacityRps : null, applied, succeeded ? null : outcome.ToString(), token);
        await transaction.CommitAsync(token);
        await registry.LoadAsync(token);
    }

    private static async Task AuditAsync(NpgsqlConnection connection, string actor, string kind, string action, Guid machine,
        Guid id, string profile, int? rate, bool applied, string? error, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO audit_log(actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,$2,$3,'Machine',$4,jsonb_build_object('calibrationId',$5::text,'profile',$6::jsonb,
                'capacityRps',$7::int,'applied',$8::boolean,'error',$9::text))
            """, connection);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(action);
        command.Parameters.AddWithValue(machine.ToString("D"));
        command.Parameters.AddWithValue(id.ToString("D"));
        command.Parameters.AddWithValue(profile);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, (object?)rate ?? DBNull.Value);
        command.Parameters.AddWithValue(applied);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)error ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidDataException("Calibration did not create exactly one audit row.");
    }
}
