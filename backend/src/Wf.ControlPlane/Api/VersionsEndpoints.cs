using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class VersionsEndpoints
{
    private static readonly WorkflowManifestJsonContext ManifestJson = new(new(WorkflowManifestJsonContext.Default.Options)
    {
        RespectRequiredConstructorParameters = true
    });

    public static IEndpointRouteBuilder MapVersionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}", Read).RequireAuthorization()
            .Produces<WorkflowVersionPlan>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Read-only task plan from one immutable version UUID, never the active version. " +
                "Workflow identity is the three exact team/type/workflow segments. Includes declared actions, " +
                "dependency order, effective placement, retries and timeouts. Excludes variable values/defaults, " +
                "input bindings, command arguments/environment, HTTP headers/bodies and storage capabilities. " +
                "Legacy tasks without action metadata explicitly have an unknown declaration.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string team, string type, string workflow,
        string version, NpgsqlDataSource source, IAuthorizationService authorization, ILogger<WorkflowVersionPlan> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        var workflowId = team + "/" + type + "/" + workflow;
        if (!WorkflowIdentity.TryGetTypeScope(workflowId, out var scope) ||
            !Guid.TryParseExact(version, "D", out var id) || id == Guid.Empty || id.ToString("D") != version ||
            context.Request.QueryString.HasValue)
            return Problem(400, "Invalid immutable workflow version identity", "invalid_version_identity");
        var token = context.RequestAborted;
        try
        {
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Type, scope, token), TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            await using var query = source.CreateCommand("""
                SELECT semver,manifest::text FROM workflow_versions WHERE workflow_id=$1 AND id=$2
                """);
            query.Parameters.AddWithValue(workflowId);
            query.Parameters.AddWithValue(id);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return Problem(404, "Workflow version not found", "version_not_found");
            var manifest = JsonSerializer.Deserialize(reader.GetString(1), ManifestJson.WorkflowManifest)
                ?? throw new InvalidDataException();
            if (manifest.Name != workflowId || manifest.Version != reader.GetString(0))
                throw new InvalidDataException();
            return Results.Json(WorkflowVersionPlan.From(id, manifest), VersionsJsonContext.Default.WorkflowVersionPlan);
        }
        catch (NpgsqlException)
        {
            logger.LogError("Immutable task plan storage is unavailable for version {VersionId}.", id);
            return Problem(503, "Workflow version data is unavailable", "version_data_unavailable");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            logger.LogError("Immutable task plan data is inconsistent for version {VersionId}.", id);
            return Problem(500, "Workflow version data is inconsistent", "version_data_inconsistent");
        }
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
