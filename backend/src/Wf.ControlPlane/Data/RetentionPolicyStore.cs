using System.Text.Json;
using Npgsql;
using Wf.ControlPlane.Api.Admin;

namespace Wf.ControlPlane.Data;

internal static class RetentionPolicyStore
{
    internal static async Task<RetentionPolicy> ReadAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.default_days,c.audit_days,
                coalesce((SELECT jsonb_object_agg(id,retention_days ORDER BY id)
                    FROM workflow_types WHERE retention_days IS NOT NULL),'{}'::jsonb)::text
            FROM retention_config c WHERE singleton
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Retention singleton is missing.");
        var perType = JsonSerializer.Deserialize(reader.GetString(2), RetentionJson.Default.DictionaryStringInt32)
            ?? throw new InvalidDataException("Retention overrides are missing.");
        var policy = new RetentionPolicy(reader.GetInt32(0), reader.GetInt32(1), perType);
        try { return policy.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid persisted retention policy.", error); }
    }
}
