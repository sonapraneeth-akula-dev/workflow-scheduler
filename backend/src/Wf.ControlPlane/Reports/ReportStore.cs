using System.Text.Json;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Reports;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core.Results;

namespace Wf.ControlPlane.Reports;

public sealed class ReportStore(NpgsqlDataSource source, IBlobStore blobs, TimeProvider time)
{
    public async Task<ReportReceipt> PublishAsync(ResultIdentity attempt, Guid reportId, ReportUploadMetadata metadata,
        Stream payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payload);
        metadata.Validate();
        if (attempt.RunId.Version != 7 || reportId.Version != 7 || attempt.Attempt <= 0)
            throw new ArgumentException("Reports require version-7 run/report IDs and a positive attempt.");
        metadata = metadata with { Tags = metadata.Tags.ToArray() };
        var content = new BlobContent(BlobKind.Reports, metadata.Sha256, metadata.SizeBytes,
            $"reports/{attempt.RunId:D}/{reportId:D}");
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Stable lock order protects global IDs and per-run admission without locking engine run-state rows.
        await LockAsync(connection, transaction, 1464226378, reportId, cancellationToken);
        await LockAsync(connection, transaction, 1464226377, attempt.RunId, cancellationToken);
        var existing = await ExistingAsync(connection, reportId, cancellationToken);
        if (existing is not null)
        {
            if (existing.RunId != attempt.RunId || existing.SourceKey != attempt.TaskKey || existing.Attempt != attempt.Attempt ||
                existing.Metadata.Kind != metadata.Kind || existing.Metadata.Title != metadata.Title ||
                existing.Metadata.SizeBytes != metadata.SizeBytes || existing.Metadata.Sha256 != metadata.Sha256 ||
                !existing.Metadata.Tags.SequenceEqual(metadata.Tags, StringComparer.Ordinal))
                throw new ReportConflictException();
        }
        else
        {
            var limits = await LimitsAsync(connection, attempt, cancellationToken);
            if (metadata.SizeBytes > limits.MaxBytes)
                throw new ReportLimitExceededException(
                    $"Report size {metadata.SizeBytes} bytes exceeds the per-report limit of {limits.MaxBytes} bytes.");
            await using var count = new NpgsqlCommand("""
                SELECT count(*) FROM reports WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                """, connection, transaction);
            count.Parameters.AddWithValue(attempt.RunId);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken))! >= limits.MaxCount)
                throw new ReportLimitExceededException($"Run report count would exceed the limit of {limits.MaxCount} reports.");
        }
        await blobs.UploadAsync(content, payload, cancellationToken);
        if (existing is null)
        {
            await using (var locator = new NpgsqlCommand(
                "INSERT INTO report_ids(id,run_day) VALUES($1,uuid_v7_run_day($2))", connection, transaction))
            {
                locator.Parameters.AddWithValue(reportId);
                locator.Parameters.AddWithValue(attempt.RunId);
                await locator.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var insert = new NpgsqlCommand("""
                INSERT INTO reports(run_day,id,run_id,source_key,attempt,kind,title,tags,size_bytes,blob_uri,sha256,created_at)
                VALUES(uuid_v7_run_day($1),$2,$1,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                """, connection, transaction);
            insert.Parameters.AddWithValue(attempt.RunId);
            insert.Parameters.AddWithValue(reportId);
            insert.Parameters.AddWithValue(attempt.TaskKey);
            insert.Parameters.AddWithValue(attempt.Attempt);
            insert.Parameters.AddWithValue(metadata.Kind);
            insert.Parameters.AddWithValue(metadata.Title);
            insert.Parameters.AddWithValue(metadata.Tags);
            insert.Parameters.AddWithValue(metadata.SizeBytes);
            insert.Parameters.AddWithValue(content.Key);
            insert.Parameters.AddWithValue(Convert.FromHexString(metadata.Sha256));
            insert.Parameters.AddWithValue(time.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new() { RunId = attempt.RunId, ReportId = reportId };
    }

    private static async Task<ReportLimits> LimitsAsync(NpgsqlConnection connection, ResultIdentity attempt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.status,a.status,ty.limits::text
            FROM runs r
            JOIN task_attempts a ON a.run_day=r.run_day AND a.run_id=r.id AND a.task_key=$2 AND a.attempt=$3
            JOIN workflows w ON w.id=r.workflow_id
            JOIN workflow_types ty ON ty.id=w.type_id
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1
            """, connection);
        command.Parameters.AddWithValue(attempt.RunId);
        command.Parameters.AddWithValue(attempt.TaskKey);
        command.Parameters.AddWithValue(attempt.Attempt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            (RunStatus)reader.GetInt16(0) is not (RunStatus.Running or RunStatus.Aborting) ||
            (ExecutionStatus)reader.GetInt16(1) is not (ExecutionStatus.Dispatched or ExecutionStatus.Running))
            throw new ReportAttemptRejectedException();
        if (reader.IsDBNull(2))
            return ReportLimits.Default;
        try
        {
            using var json = JsonDocument.Parse(reader.GetString(2));
            return ReportLimits.FromTypeLimits(json.RootElement);
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            throw new InvalidOperationException("Stored workflow type report policy is invalid.", error);
        }
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int scope,
        Guid id, CancellationToken cancellationToken)
    {
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1,hashtext($2))", connection, transaction);
        gate.Parameters.AddWithValue(scope);
        gate.Parameters.AddWithValue(id.ToString("D"));
        await gate.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Existing?> ExistingAsync(NpgsqlConnection connection, Guid reportId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.source_key,r.attempt,r.kind,r.title,r.tags,encode(r.sha256,'hex'),r.size_bytes,r.run_id
            FROM report_ids i JOIN reports r ON r.run_day=i.run_day AND r.id=i.id WHERE i.id=$1
            """, connection);
        command.Parameters.AddWithValue(reportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return !await reader.ReadAsync(cancellationToken) ? null :
            new(reader.GetGuid(7), reader.GetString(0), reader.GetInt16(1), new(reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<string[]>(4), reader.GetString(5), reader.GetInt64(6)));
    }

    private sealed record Existing(Guid RunId, string SourceKey, short Attempt, ReportUploadMetadata Metadata);
}

public sealed class ReportConflictException : Exception;
public sealed class ReportAttemptRejectedException : Exception;
