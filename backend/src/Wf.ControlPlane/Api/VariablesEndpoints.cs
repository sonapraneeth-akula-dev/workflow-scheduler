using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Registry;

namespace Wf.ControlPlane.Api;

public static class VariablesEndpoints
{
    public static IEndpointRouteBuilder MapVariablesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/workflows/{team}/{type}/{workflow}/variables", Read).RequireAuthorization()
            .Produces<WorkflowVariablesResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Trigger variable schema and literal defaults for an Operator or Admin on the exact type scope. " +
                "Optional version is one canonical UUID belonging to this undeleted workflow; omitted selects its current active version. " +
                "No other query parameters are supported. Secrets contain names only; no secret resolution or task configuration is exposed.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string team, string type, string workflow,
        [AsParameters] WorkflowVariablesQuery query, NpgsqlDataSource source, IAuthorizationService authorization,
        ILogger<WorkflowVariablesResponse> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var workflowId = team + "/" + type + "/" + workflow;
        Guid? version = null;
        if (!WorkflowIdentity.TryGetTypeScope(workflowId, out var scope) ||
            context.Request.Query.Any(pair => pair.Key != "version" || pair.Value.Count != 1))
            return Problem(400, "Invalid workflow variable schema identity or query", "invalid_variables_query");
        if (query.Version is not null)
        {
            if (!Guid.TryParseExact(query.Version, "D", out var id) || id == Guid.Empty || id.ToString("D") != query.Version)
                return Problem(400, "Use one canonical workflow version UUID", "invalid_variables_query");
            version = id;
        }
        try
        {
            var token = context.RequestAborted;
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Type, scope, token), TeamAuthorizationExtensions.Policy(RegistryRole.Operator));
            if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            var selected = await WorkflowVersionResolver.ResolveAsync(source, workflowId, version, token);
            return Results.Json(WorkflowVariablesResponse.From(selected.VersionId, selected.Manifest),
                VariablesJsonContext.Default.WorkflowVariablesResponse);
        }
        catch (KeyNotFoundException) { return Problem(404, "Workflow version not found", "version_not_found"); }
        catch (NpgsqlException)
        {
            logger.LogError("Workflow variable schema storage is unavailable for version {VersionId}.", version);
            return Problem(503, "Workflow version data is unavailable", "version_data_unavailable");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            logger.LogError("Workflow variable schema data is inconsistent for version {VersionId}.", version);
            return Problem(500, "Workflow version data is inconsistent", "version_data_inconsistent");
        }
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
