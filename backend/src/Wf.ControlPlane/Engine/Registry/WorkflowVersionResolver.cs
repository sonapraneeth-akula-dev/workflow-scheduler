using System.Text.Json;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Validation.SemVer;

namespace Wf.ControlPlane.Engine.Registry;

public sealed record SelectedWorkflowVersion(Guid VersionId, WorkflowManifest Manifest);

public static class WorkflowVersionResolver
{
    public static async Task<SelectedWorkflowVersion> ResolveAsync(ActiveVersionCache cache, NpgsqlDataSource source,
        string workflowId, string? version, bool allowRanges, CancellationToken cancellationToken,
        NpgsqlConnection? connection = null)
    {
        if (!cache.IsReady) throw new InvalidOperationException("Workflow versions are synchronizing.");
        if (version is null && connection is null)
        {
            var active = cache.GetActive(workflowId);
            return new(active.VersionId, active.Manifest);
        }
        var isId = Guid.TryParseExact(version, "D", out var id);
        VersionConstraint? constraint = null;
        if (version is not null && !isId)
        {
            try
            {
                if (version.Length > 256) throw new ArgumentException("Version is too long.");
                _ = new ApiBaseline(version, [], []);
            }
            catch (ArgumentException) when (allowRanges) { constraint = VersionConstraint.Parse(version); }
        }
        await using var owned = connection is null ? await source.OpenConnectionAsync(cancellationToken) : null;
        var database = connection ?? owned!;
        if (constraint is not null)
        {
            var candidates = new Dictionary<string, Guid>(StringComparer.Ordinal);
            await using (var query = new NpgsqlCommand("""
                SELECT v.id,v.semver FROM workflow_versions v JOIN workflows w ON w.id=v.workflow_id
                WHERE w.id=$1 AND w.deleted_at IS NULL
                """, database))
            {
                query.Parameters.AddWithValue(workflowId);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    candidates.Add(reader.GetString(1), reader.GetGuid(0));
            }
            var selected = constraint.SelectHighest(candidates.Keys)
                ?? throw new KeyNotFoundException($"Workflow '{workflowId}' has no matching version.");
            id = candidates[selected];
            isId = true;
        }
        await using var read = new NpgsqlCommand($"""
            SELECT v.id,v.semver,v.manifest::text FROM workflow_versions v
            JOIN workflows w ON w.id=v.workflow_id
            WHERE w.id=$1 AND w.deleted_at IS NULL AND
                {(version is null ? "v.id=w.active_version_id" : isId ? "v.id=$2" : "v.semver_key=split_part($2,'+',1)")}
            """, database);
        read.Parameters.AddWithValue(workflowId);
        if (version is not null)
        {
            if (isId) read.Parameters.AddWithValue(id);
            else read.Parameters.AddWithValue(version);
        }
        await using var result = await read.ExecuteReaderAsync(cancellationToken);
        if (!await result.ReadAsync(cancellationToken))
            throw new KeyNotFoundException($"Workflow '{workflowId}' has no matching version.");
        var manifest = JsonSerializer.Deserialize(result.GetString(2), WorkflowManifestJsonContext.Default.WorkflowManifest)
            ?? throw new InvalidDataException("The selected workflow manifest is null.");
        if (manifest.Name != workflowId || manifest.Version != result.GetString(1))
            throw new InvalidDataException("The selected workflow manifest does not match its stored identity.");
        return new(result.GetGuid(0), manifest);
    }
}
