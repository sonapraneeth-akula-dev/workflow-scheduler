using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Validation;
using Wf.Validation.Schedules;

namespace Wf.ControlPlane.Registration;

public sealed record ScheduleFileApplication(Guid? PreviousVersion, Guid? ChangedVersion);

/// <summary>Runs inside the registration team's advisory lock and mutation transaction.</summary>
public static class ScheduleFileApplier
{
    public static void ValidateRequest(RegistrationRequest request)
    {
        if (request.Schedules is null)
        {
            if (request.ScheduleSourcePath is not null)
                throw Invalid("$.scheduleSourcePath", "A source path must accompany a schedule file.");
            return;
        }
        if (request.WorkflowId is null)
            throw Invalid("$.workflowId", "A workflow identity is required with a schedule file.");
        var path = request.ScheduleSourcePath;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.Any(character => character is '\\' or ':' || char.IsControl(character)) ||
            path.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            throw Invalid("$.scheduleSourcePath", "Use a repository-relative path of at most 1024 characters, without dot segments or control characters.");
        byte[] utf8;
        try { utf8 = new UTF8Encoding(false, true).GetBytes(request.Schedules); }
        catch (EncoderFallbackException) { throw Invalid("$.schedules", "Schedule content must be valid UTF-8 text."); }
        if (utf8.Length is 0 or > 1024 * 1024 || request.Schedules.Contains('\0'))
            throw Invalid("$.schedules", "Schedule content must contain 1 byte to 1 MiB of UTF-8 text without NUL characters.");
    }

    public static async Task<ScheduleFileApplication> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RegistrationRequest request, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid? previous;
        string? persisted;
        string? persistedPath;
        WorkflowManifest manifest;
        await using (var read = new NpgsqlCommand("""
            SELECT w.deleted_at,w.active_schedule_config_id,c.content,c.source_path,v.manifest::text
            FROM workflows w LEFT JOIN workflow_versions v ON v.id=w.active_version_id
            LEFT JOIN schedule_config_versions c ON c.id=w.active_schedule_config_id
            WHERE w.id=$1 FOR UPDATE OF w
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(request.WorkflowId!);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.IsDBNull(0) || reader.IsDBNull(4))
                throw Invalid("$.workflowId", "Register a non-deleted workflow with an active version before applying schedules.");
            previous = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            persisted = reader.IsDBNull(2) ? null : reader.GetString(2);
            persistedPath = reader.IsDBNull(3) ? null : reader.GetString(3);
            manifest = JsonSerializer.Deserialize(reader.GetString(4), WorkflowManifestJsonContext.Default.WorkflowManifest)
                ?? throw new InvalidDataException("The active workflow manifest is null.");
        }
        var content = request.Schedules ?? persisted;
        if (content is null)
        {
            if (previous is not null)
                throw new RegistrationConflictException("The active schedule config has no retained file; register the schedule file with this workflow change.");
            return new(previous, null);
        }
        var path = request.ScheduleSourcePath ?? persistedPath!;
        var utf8 = Encoding.UTF8.GetBytes(content);
        var validated = ScheduleFileValidator.Validate(utf8, manifest.Variables, path, cancellationToken);
        if (!validated.IsValid) throw new RegistrationValidationException(validated.Errors);
        var hash = SHA256.HashData(utf8);
        Guid config;
        await using (var snapshot = new NpgsqlCommand("""
            INSERT INTO schedule_config_versions(id,workflow_id,source_path,git_commit,content_sha256,git_author,registered_by,content)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8) ON CONFLICT(workflow_id,content_sha256) DO NOTHING RETURNING id
            """, connection, transaction))
        {
            snapshot.Parameters.AddWithValue(Guid.CreateVersion7());
            snapshot.Parameters.AddWithValue(request.WorkflowId!);
            snapshot.Parameters.AddWithValue(path);
            snapshot.Parameters.AddWithValue(request.GitCommit);
            snapshot.Parameters.AddWithValue(hash);
            snapshot.Parameters.AddWithValue(request.GitAuthor);
            snapshot.Parameters.AddWithValue(actor);
            snapshot.Parameters.AddWithValue(content);
            if (await snapshot.ExecuteScalarAsync(cancellationToken) is Guid inserted) config = inserted;
            else
            {
                await using var existing = new NpgsqlCommand(
                    "SELECT id FROM schedule_config_versions WHERE workflow_id=$1 AND content_sha256=$2", connection, transaction);
                existing.Parameters.AddWithValue(request.WorkflowId!);
                existing.Parameters.AddWithValue(hash);
                config = (Guid)(await existing.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidDataException("The schedule snapshot disappeared inside registration."));
            }
        }
        await using (var retain = new NpgsqlCommand(
            "UPDATE schedule_config_versions SET content=$2 WHERE id=$1 AND content IS NULL", connection, transaction))
        {
            retain.Parameters.AddWithValue(config);
            retain.Parameters.AddWithValue(content);
            await retain.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var instance in validated.Instances.OrderBy(instance => instance.Definition.Id, StringComparer.Ordinal))
        {
            var definition = instance.Definition;
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO schedules(id,workflow_id,origin,code_key,name,timezone,cron,interval_s,variables,version_pin,
                    overlap_policy,catchup_policy,next_fire_at,created_by,updated_by,config_version_id,created_at,updated_at)
                VALUES($1,$2,0,$3,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$12,$13,$14,$14)
                ON CONFLICT(workflow_id,code_key) WHERE origin=0 DO UPDATE SET
                    timezone=excluded.timezone,cron=excluded.cron,interval_s=excluded.interval_s,variables=excluded.variables,
                    version_pin=excluded.version_pin,overlap_policy=excluded.overlap_policy,catchup_policy=excluded.catchup_policy,
                    config_version_id=excluded.config_version_id,updated_by=excluded.updated_by,updated_at=excluded.updated_at,
                    deleted_at=NULL,paused=CASE WHEN schedules.deleted_at IS NULL THEN schedules.paused ELSE false END,
                    next_fire_at=CASE WHEN schedules.deleted_at IS NULL
                        AND (schedules.timezone,schedules.cron,schedules.interval_s)
                            IS NOT DISTINCT FROM (excluded.timezone,excluded.cron,excluded.interval_s)
                        THEN schedules.next_fire_at ELSE excluded.next_fire_at END,
                    row_version=schedules.row_version+1
                WHERE schedules.deleted_at IS NOT NULL OR
                    (schedules.timezone,schedules.cron,schedules.interval_s,schedules.variables,schedules.version_pin,
                        schedules.overlap_policy,schedules.catchup_policy,schedules.config_version_id)
                    IS DISTINCT FROM
                    (excluded.timezone,excluded.cron,excluded.interval_s,excluded.variables,excluded.version_pin,
                        excluded.overlap_policy,excluded.catchup_policy,excluded.config_version_id)
                """, connection, transaction);
            upsert.Parameters.AddWithValue(Guid.CreateVersion7());
            upsert.Parameters.AddWithValue(request.WorkflowId!);
            upsert.Parameters.AddWithValue(definition.Id);
            upsert.Parameters.AddWithValue(definition.Timezone);
            upsert.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)definition.Cron ?? DBNull.Value);
            upsert.Parameters.AddWithValue(NpgsqlDbType.Integer, definition.Interval is { } interval
                ? checked((int)(interval.Ticks / TimeSpan.TicksPerSecond)) : DBNull.Value);
            upsert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(instance.Variables));
            upsert.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)definition.VersionPin ?? DBNull.Value);
            upsert.Parameters.AddWithValue((short)definition.Overlap);
            upsert.Parameters.AddWithValue((short)definition.CatchUp);
            upsert.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)instance.Expression.GetNextOccurrence(now) ?? DBNull.Value);
            upsert.Parameters.AddWithValue(actor);
            upsert.Parameters.AddWithValue(config);
            upsert.Parameters.AddWithValue(now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var remove = new NpgsqlCommand("""
            UPDATE schedules SET deleted_at=$3,paused=true,next_fire_at=NULL,row_version=row_version+1,
                updated_at=$3,updated_by=$4
            WHERE workflow_id=$1 AND origin=0 AND deleted_at IS NULL AND NOT (code_key=ANY($2))
            """, connection, transaction))
        {
            remove.Parameters.AddWithValue(request.WorkflowId!);
            remove.Parameters.AddWithValue(validated.Instances.Select(instance => instance.Definition.Id).ToArray());
            remove.Parameters.AddWithValue(now);
            remove.Parameters.AddWithValue(actor);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var series = new NpgsqlCommand("""
            INSERT INTO run_series(id,workflow_id,schedule_id)
            SELECT gen_random_uuid(),workflow_id,id FROM schedules WHERE workflow_id=$1 AND origin=0
            ON CONFLICT(schedule_id) DO NOTHING
            """, connection, transaction))
        {
            series.Parameters.AddWithValue(request.WorkflowId!);
            await series.ExecuteNonQueryAsync(cancellationToken);
        }
        if (previous != config)
        {
            await using var activate = new NpgsqlCommand(
                "UPDATE workflows SET active_schedule_config_id=$2 WHERE id=$1", connection, transaction);
            activate.Parameters.AddWithValue(request.WorkflowId!);
            activate.Parameters.AddWithValue(config);
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }
        return new(previous, previous != config ? config : null);
    }

    private static RegistrationValidationException Invalid(string path, string hint) =>
        new([new("REG001", "registration.json", path, "The registration request is invalid.", hint)]);
}
