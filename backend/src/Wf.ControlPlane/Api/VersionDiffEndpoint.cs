using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;
using Wf.ControlPlane.Versions;
using Wf.Validation;

namespace Wf.ControlPlane.Api;

public sealed record WorkflowVersionPair(Guid From, Guid To)
{
    public static WorkflowVersionPair Parse(IQueryCollection query)
    {
        if (query.Count != 2 || query.Any(entry => entry.Key is not ("from" or "to") || entry.Value.Count != 1))
            throw new ArgumentException("Supply exactly one from and one to immutable version UUID.");
        return new(ParseId(query["from"][0]), ParseId(query["to"][0]));
    }

    private static Guid ParseId(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
            ? id : throw new ArgumentException("Use canonical nonempty immutable version UUIDs.");
}

public static class VersionDiffEndpoint
{
    private static readonly WorkflowManifestJsonContext ManifestJson = new(new(WorkflowManifestJsonContext.Default.Options)
    {
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    });

    internal static void MapVersionDiff(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/workflows/{team}/{type}/{workflow}/diff", Read).RequireAuthorization()
            .Produces<WorkflowVersionDiff>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Fresh Viewer-authorized comparison of two exact immutable version UUIDs in the same workflow, " +
                "including inactive and soft-deleted versions. Keyed node/variable changes, workflow policies and settings " +
                "include before/after manifest declarations. Source contains exact retained workflow.cs texts for a client diff editor. " +
                "Includes authored literal configuration/source, but never resolves secrets or returns storage capabilities. " +
                "No active-version fallback. Both source archives are SHA-256/length verified and bounded to 1 MiB of UTF-8 per script.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "from", "to" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                    });
                return Task.CompletedTask;
            });

    private static async Task<IResult> Read(HttpContext context, string team, string type, string workflow,
        NpgsqlDataSource source, IAuthorizationService authorization, [FromServices] IBlobStore blobs,
        ILogger<WorkflowVersionDiff> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        var workflowId = team + "/" + type + "/" + workflow;
        if (!WorkflowIdentity.TryGetTypeScope(workflowId, out var scope))
            return Problem(400, "Invalid workflow identity", "invalid_version_identity");
        WorkflowVersionPair pair;
        try { pair = WorkflowVersionPair.Parse(context.Request.Query); }
        catch (ArgumentException)
        {
            return Problem(400, "Supply exactly one canonical from and to version UUID", "invalid_version_query");
        }
        var token = context.RequestAborted;
        try
        {
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Type, scope, token), TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            var versions = new Dictionary<Guid, StoredVersion>();
            await using (var query = source.CreateCommand("""
                SELECT id,semver,manifest::text,source_uri,source_sha256,source_size
                FROM workflow_versions WHERE workflow_id=$1 AND (id=$2 OR id=$3)
                """))
            {
                query.Parameters.AddWithValue(workflowId);
                query.Parameters.AddWithValue(pair.From);
                query.Parameters.AddWithValue(pair.To);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var manifest = JsonSerializer.Deserialize(reader.GetString(2), ManifestJson.WorkflowManifest)
                        ?? throw new InvalidDataException();
                    if (manifest.Name != workflowId || manifest.Version != reader.GetString(1) ||
                        !Validator.Validate(manifest, cancellationToken: token).IsValid)
                        throw new InvalidDataException();
                    var content = new BlobContent(BlobKind.Sources, Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(4)),
                        reader.GetInt64(5));
                    if (reader.GetString(3) != content.Key) throw new InvalidDataException();
                    versions.Add(reader.GetGuid(0), new(manifest, content));
                }
            }
            if (!versions.TryGetValue(pair.From, out var before) || !versions.TryGetValue(pair.To, out var after))
                return Problem(404, "Workflow version pair not found", "version_not_found");
            var oldSource = await VersionSourceReader.ReadAsync(blobs, before.Source, token);
            var newSource = before.Source.Key == after.Source.Key ? oldSource :
                await VersionSourceReader.ReadAsync(blobs, after.Source, token);
            // Equal content keys must still agree on expected size.
            if (before.Source.Key == after.Source.Key && before.Source.SizeBytes != after.Source.SizeBytes)
                throw new InvalidDataException();
            return Results.Json(new WorkflowVersionDiff(workflowId, pair.From, pair.To,
                before.Manifest.Version, after.Manifest.Version, ManifestDiff.Compare(before.Manifest, after.Manifest),
                new("workflow.cs", oldSource, newSource, oldSource != newSource)), VersionsJsonContext.Default.WorkflowVersionDiff);
        }
        catch (NpgsqlException)
        {
            logger.LogError("Workflow version comparison database is unavailable.");
            return Problem(503, "Workflow version data is unavailable", "version_data_unavailable");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or InvalidCastException)
        {
            logger.LogError("Workflow version comparison data is inconsistent.");
            return Problem(500, "Workflow version data is inconsistent", "version_data_inconsistent");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Azure.RequestFailedException)
        {
            logger.LogError("Workflow version comparison source storage is unavailable.");
            return Problem(503, "Workflow source data is unavailable", "version_source_unavailable");
        }
    }

    private sealed record StoredVersion(WorkflowManifest Manifest, BlobContent Source);
    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
