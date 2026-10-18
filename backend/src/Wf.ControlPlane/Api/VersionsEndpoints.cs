using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
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
        endpoints.MapVersionDiff();
        endpoints.MapVersionClone();
        endpoints.MapVersionActivation();
        endpoints.MapGet("/api/v1/workflows/{team}/{type}/{workflow}/versions", List).RequireAuthorization()
            .Produces<WorkflowVersionPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Fresh Viewer-authorized registration history, including inactive and soft-deleted workflows. " +
                "Newest registration timestamp first, then descending immutable UUID for ties. " +
                "Cursor is bound to exact workflow identity and page limit; recheck authorization on every page. " +
                "Lists SemVer, Git author/commit, registering identity, change note, SDK version and registration time, " +
                "plus active/latest version pointers from the same database snapshot. No source, variable values or storage capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "limit", In = ParameterLocation.Query,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100", Default = JsonValue.Create(50) }
                });
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "cursor", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                return Task.CompletedTask;
            });
        endpoints.MapGet("/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}", Read).RequireAuthorization()
            .Produces<WorkflowVersionPlan>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Read-only task plan from one immutable version UUID, never the active version. " +
                "Workflow identity is the three exact team/type/workflow segments. Includes declared actions, " +
                "dependency order, effective placement, retries and timeouts. Excludes variable values/defaults, " +
                "input bindings, command arguments/environment, HTTP headers/bodies and storage capabilities. " +
                "Legacy tasks without action metadata explicitly have an unknown declaration. " +
                "Registration includes immutable Git author/commit, registering identity, change note, SDK version and timestamp.");
        return endpoints;
    }

    private static async Task<IResult> List(HttpContext context, string team, string type, string workflow,
        NpgsqlDataSource source, IAuthorizationService authorization, ILogger<WorkflowVersionPage> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        WorkflowVersionsQuery filter;
        var workflowId = team + "/" + type + "/" + workflow;
        try { filter = WorkflowVersionsQuery.Parse(workflowId, context.Request.Query); }
        catch (ArgumentException error)
        {
            return Results.Problem(statusCode: 400, title: "Invalid workflow version query", detail: error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_version_query" });
        }
        var token = context.RequestAborted;
        try
        {
            WorkflowIdentity.TryGetTypeScope(workflowId, out var scope);
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Type, scope, token), TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            await using var query = source.CreateCommand("""
                SELECT w.active_version_id,w.latest_version_id,v.id,v.semver,
                    v.git_commit,v.git_author,v.registered_by,v.change_note,v.sdk_version,v.created_at
                FROM workflows w LEFT JOIN LATERAL (
                    SELECT id,semver,git_commit,git_author,registered_by,change_note,sdk_version,created_at
                    FROM workflow_versions WHERE workflow_id=w.id
                        AND ($2::timestamptz IS NULL OR (created_at,id)<($2,$3))
                    ORDER BY created_at DESC,id DESC LIMIT $4
                ) v ON true WHERE w.id=$1 ORDER BY v.created_at DESC,v.id DESC
                """);
            query.Parameters.AddWithValue(workflowId);
            query.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)filter.After?.At ?? DBNull.Value);
            query.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)filter.After?.Id ?? DBNull.Value);
            query.Parameters.AddWithValue(filter.Limit + 1);
            var items = new List<WorkflowVersionSummary>();
            Guid? active = null;
            Guid? latest = null;
            var found = false;
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                found = true;
                active = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                latest = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                if (!reader.IsDBNull(2))
                    items.Add(new(reader.GetGuid(2), workflowId, reader.GetString(3), Registration(reader, 4)));
            }
            if (!found) return Problem(404, "Workflow not found", "workflow_not_found");
            string? cursor = null;
            if (items.Count > filter.Limit)
            {
                items.RemoveAt(items.Count - 1);
                var last = items[^1];
                cursor = filter.Cursor(last.Registration.RegisteredAt, last.VersionId);
            }
            return Results.Json(new WorkflowVersionPage(items.ToArray(), cursor, active, latest),
                VersionsJsonContext.Default.WorkflowVersionPage);
        }
        catch (NpgsqlException)
        {
            logger.LogError("Workflow version history storage is unavailable.");
            return Problem(503, "Workflow version data is unavailable", "version_data_unavailable");
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or InvalidCastException)
        {
            logger.LogError("Workflow version history data is inconsistent.");
            return Problem(500, "Workflow version data is inconsistent", "version_data_inconsistent");
        }
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
                SELECT semver,manifest::text,git_commit,git_author,registered_by,change_note,sdk_version,created_at
                FROM workflow_versions WHERE workflow_id=$1 AND id=$2
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
            return Results.Json(WorkflowVersionPlan.From(id, manifest) with { Registration = Registration(reader, 2) },
                VersionsJsonContext.Default.WorkflowVersionPlan);
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

    private static WorkflowVersionRegistration Registration(NpgsqlDataReader reader, int offset) =>
        new(reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2),
            reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3), reader.GetString(offset + 4),
            reader.GetFieldValue<DateTimeOffset>(offset + 5));

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
