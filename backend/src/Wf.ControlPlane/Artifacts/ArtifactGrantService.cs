using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Artifacts;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Artifacts;

public sealed class ArtifactGrantService(NpgsqlDataSource source, IBlobStore blobs, TimeProvider time)
{
    public async Task<ArtifactGrant> GrantAsync(ArtifactActor actor, Guid id, ArtifactMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        metadata.Validate();
        if (id.Version != 7) throw new ArgumentException("Artifact IDs must be UUIDv7.", nameof(id));
        if (metadata.SizeBytes > ArtifactMetadata.RunBudget) throw new ArtifactLimitExceededException();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Lock(connection, id, cancellationToken, 1464226381);
        await Lock(connection, actor.RunId, cancellationToken);
        var existing = await Find(connection, id, cancellationToken);
        await Authorize(connection, actor, cancellationToken, existing?.Committed == true);
        if (existing is not null)
        {
            Check(existing, actor, metadata);
            if (existing.Committed || existing.ExpiresAt > time.GetUtcNow())
                return new(id, existing.ExpiresAt, existing.Committed);
        }
        await using (var expire = new NpgsqlCommand(
            "DELETE FROM artifact_grants WHERE run_id=$1 AND NOT committed AND expires_at<=$2", connection))
        {
            expire.Parameters.AddWithValue(actor.RunId);
            expire.Parameters.AddWithValue(time.GetUtcNow());
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var total = new NpgsqlCommand(
            "SELECT COALESCE(sum(size_bytes),0)::bigint FROM artifact_grants WHERE run_id=$1", connection))
        {
            total.Parameters.AddWithValue(actor.RunId);
            var used = (long)(await total.ExecuteScalarAsync(cancellationToken))!;
            if (metadata.SizeBytes > ArtifactMetadata.RunBudget - used) throw new ArtifactLimitExceededException();
        }
        var expires = new DateTimeOffset(Math.Min(time.GetUtcNow().AddMinutes(15).UtcTicks, actor.ExpiresUtcTicks), TimeSpan.Zero);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO artifact_grants(id,run_day,run_id,source_key,attempt,agent_id,session_id,reservation_epoch,
                name,content_type,size_bytes,sha256,expires_at)
            VALUES($1,uuid_v7_run_day($2),$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            """, connection);
        Add(insert, id, actor.RunId, actor.SourceKey, actor.Attempt, actor.AgentId, actor.SessionId,
            actor.ReservationEpoch, metadata.Name, metadata.ContentType, metadata.SizeBytes,
            Convert.FromHexString(metadata.Sha256), expires);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(id, expires, false);
    }

    public async Task<ArtifactReceipt> PublishAsync(ArtifactActor actor, Guid id, Stream payload, long? contentLength,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Admission and expiry cleanup take this same lock. An upload keeps its reservation counted
        // for its entire lifetime, even if its grant expires while bytes are still arriving.
        await Lock(connection, id, cancellationToken, 1464226381);
        await Lock(connection, actor.RunId, cancellationToken);
        var grant = await Find(connection, id, cancellationToken) ?? throw new ArtifactAttemptRejectedException();
        await Authorize(connection, actor, cancellationToken, grant.Committed);
        Check(grant, actor, grant.Metadata);
        if (!grant.Committed && grant.ExpiresAt <= time.GetUtcNow()) throw new ArtifactAttemptRejectedException();
        if (contentLength != grant.Metadata.SizeBytes) throw new InvalidDataException("Artifact length does not match its grant.");
        var content = new BlobContent(BlobKind.Artifacts, grant.Metadata.Sha256, grant.Metadata.SizeBytes);
        await LockContentAsync(connection, grant.Metadata.Sha256, cancellationToken);
        await blobs.UploadAsync(content, payload, cancellationToken);
        await Authorize(connection, actor, cancellationToken, grant.Committed);
        if (!grant.Committed)
        {
            await using var commit = new NpgsqlCommand("""
                WITH inserted AS (
                    INSERT INTO artifacts(run_day,id,run_id,source_key,name,content_type,size_bytes,blob_uri,sha256,created_at)
                    VALUES(uuid_v7_run_day($1),$2,$1,$3,$4,$5,$6,$7,$8,$9)
                    RETURNING id
                )
                UPDATE artifact_grants SET committed=true WHERE id IN (SELECT id FROM inserted)
                """, connection);
            Add(commit, actor.RunId, id, actor.SourceKey, grant.Metadata.Name, grant.Metadata.ContentType,
                content.SizeBytes, content.Key, Convert.FromHexString(content.Sha256), time.GetUtcNow());
            await commit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(actor.RunId, id);
    }

    public async Task ReleaseAsync(ArtifactActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Lock(connection, id, cancellationToken, 1464226381);
        await Lock(connection, actor.RunId, cancellationToken);
        var grant = await Find(connection, id, cancellationToken);
        if (grant is not null)
        {
            Check(grant, actor, grant.Metadata);
            await using var release = new NpgsqlCommand("DELETE FROM artifact_grants WHERE id=$1 AND NOT committed", connection);
            release.Parameters.AddWithValue(id);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task Authorize(NpgsqlConnection connection, ArtifactActor actor, CancellationToken token, bool committed = false)
    {
        await using var query = new NpgsqlCommand("""
            SELECT a.dispatch_payload FROM task_attempts a
            JOIN runs r ON r.run_day=a.run_day AND r.id=a.run_id
            JOIN agents g ON g.id=a.agent_id
            WHERE a.run_day=uuid_v7_run_day($1) AND a.run_id=$1 AND a.task_key=$2 AND a.attempt=$3
                AND a.agent_id=$4 AND g.session_id=$5 AND ($6 OR (a.status IN (2,3) AND r.status IN (1,2)
                    AND NOT EXISTS (SELECT 1 FROM task_attempts newer WHERE newer.run_day=a.run_day AND
                        newer.run_id=a.run_id AND newer.task_key=a.task_key AND newer.attempt>a.attempt)))
            """, connection);
        Add(query, actor.RunId, actor.SourceKey, actor.Attempt, actor.AgentId, actor.SessionId, committed);
        if (actor.ExpiresUtcTicks <= time.GetUtcNow().UtcTicks ||
            await query.ExecuteScalarAsync(token) is not byte[] bytes ||
            Dispatch.Parser.ParseFrom(bytes).ReservationEpoch != (ulong)actor.ReservationEpoch)
            throw new ArtifactAttemptRejectedException();
    }

    private static async Task Lock(NpgsqlConnection connection, Guid runId, CancellationToken token, int scope = 1464226380)
    {
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1,hashtext($2))", connection);
        gate.Parameters.AddWithValue(scope);
        gate.Parameters.AddWithValue(runId.ToString("D"));
        await gate.ExecuteNonQueryAsync(token);
    }

    internal static async Task LockContentAsync(NpgsqlConnection connection, string sha256, CancellationToken token)
    {
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1464226382,hashtext($1))", connection);
        gate.Parameters.AddWithValue(sha256);
        await gate.ExecuteNonQueryAsync(token);
    }

    private static void Check(Reservation grant, ArtifactActor actor, ArtifactMetadata metadata)
    {
        if (grant.RunId != actor.RunId || grant.SourceKey != actor.SourceKey || grant.Attempt != actor.Attempt ||
            grant.AgentId != actor.AgentId || grant.SessionId != actor.SessionId ||
            grant.Epoch != actor.ReservationEpoch) throw new ArtifactAttemptRejectedException();
        if (grant.Metadata != metadata) throw new ArtifactConflictException();
    }

    private static async Task<Reservation?> Find(NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var query = new NpgsqlCommand("""
            SELECT run_id,source_key,attempt,agent_id,session_id,reservation_epoch,name,content_type,
                size_bytes,encode(sha256,'hex'),expires_at,committed FROM artifact_grants WHERE id=$1
            """, connection);
        query.Parameters.AddWithValue(id);
        await using var row = await query.ExecuteReaderAsync(token);
        return !await row.ReadAsync(token) ? null : new(row.GetGuid(0), row.GetString(1), row.GetInt16(2),
            row.GetGuid(3), row.GetGuid(4), row.GetInt64(5),
            new(row.GetString(6), row.GetString(7), row.GetInt64(8), row.GetString(9)),
            row.GetFieldValue<DateTimeOffset>(10), row.GetBoolean(11));
    }

    private static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values) command.Parameters.AddWithValue(value);
    }

    private sealed record Reservation(Guid RunId, string SourceKey, short Attempt, Guid AgentId, Guid SessionId,
        long Epoch, ArtifactMetadata Metadata, DateTimeOffset ExpiresAt, bool Committed);
}

public sealed class ArtifactAttemptRejectedException : Exception;
public sealed class ArtifactConflictException : Exception;
