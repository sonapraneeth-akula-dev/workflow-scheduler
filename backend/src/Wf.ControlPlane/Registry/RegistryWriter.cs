using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Registry;
using Wf.Validation;
using Wf.Validation.Rules;

namespace Wf.ControlPlane.Registry;

public sealed class RegistryValidationException(ImmutableArray<ValidationError> errors)
    : Exception("The team registry failed shared validation.")
{
    public ImmutableArray<ValidationError> Errors { get; } = errors;
}

/// <summary>Writes a complete team snapshot inside the caller's registration transaction; never commits it.</summary>
public sealed class RegistryWriter
{
    private readonly int _typeQuota;

    public RegistryWriter(int typeQuota = TeamRegistryRules.DefaultTypeQuota)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(typeQuota);
        _typeQuota = typeQuota;
    }

    public async Task<bool> UpsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        TeamRegistry registry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(registry);
        if (transaction.Connection != connection)
            throw new ArgumentException("A live transaction on the supplied connection is required.", nameof(transaction));
        var errors = TeamRegistryRules.Validate(registry, typeQuota: _typeQuota, cancellationToken: cancellationToken);
        if (!errors.IsEmpty) throw new RegistryValidationException(errors);
        var json = JsonSerializer.Serialize(registry, TeamRegistryJsonContext.Default.TeamRegistry);
        using var document = JsonDocument.Parse(json);
        var presentation = document.RootElement.TryGetProperty("presentation", out var configured)
            ? configured.GetRawText() : "{}";

        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('wf.registry:' || current_schema() || ':' || $1,0))",
            connection, transaction))
        {
            gate.Parameters.AddWithValue(registry.Team);
            await gate.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var team = new NpgsqlCommand("""
            INSERT INTO teams(id,name,presentation,registry) VALUES($1,$1,$2,$3)
            ON CONFLICT(id) DO UPDATE SET name=EXCLUDED.name,presentation=EXCLUDED.presentation,registry=EXCLUDED.registry
            WHERE teams.registry IS DISTINCT FROM EXCLUDED.registry
            RETURNING id
            """, connection, transaction))
        {
            team.Parameters.AddWithValue(registry.Team);
            team.Parameters.AddWithValue(NpgsqlDbType.Jsonb, presentation);
            team.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json);
            if (await team.ExecuteScalarAsync(cancellationToken) is null) return false;
        }
        await using (var remove = new NpgsqlCommand(
            "DELETE FROM workflow_types WHERE team_id=$1 AND NOT(name=ANY($2))", connection, transaction))
        {
            remove.Parameters.AddWithValue(registry.Team);
            remove.Parameters.AddWithValue(registry.Types.Select(type => type.Name).ToArray());
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var index = 0; index < registry.Types.Length; index++)
        {
            var type = registry.Types[index];
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
                VALUES($1,$2,$3,$4,$5,$6)
                ON CONFLICT(id) DO UPDATE SET normalized_name=EXCLUDED.normalized_name,
                    description=EXCLUDED.description,metadata_schema=EXCLUDED.metadata_schema
                """, connection, transaction);
            upsert.Parameters.AddWithValue(registry.Team + "/" + type.Name);
            upsert.Parameters.AddWithValue(registry.Team);
            upsert.Parameters.AddWithValue(type.Name);
            upsert.Parameters.AddWithValue(TypeNameSimilarity.Normalize(type.Name));
            upsert.Parameters.AddWithValue(type.Description);
            upsert.Parameters.AddWithValue(NpgsqlDbType.Jsonb,
                document.RootElement.GetProperty("types")[index].GetProperty("metadataSchema").GetRawText());
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var notify = new NpgsqlCommand("SELECT pg_notify('registry',$1)", connection, transaction);
        notify.Parameters.AddWithValue(registry.Team);
        await notify.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }
}
