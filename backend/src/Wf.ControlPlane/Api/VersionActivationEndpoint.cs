using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Auth;
using Wf.Validation;

namespace Wf.ControlPlane.Api;

public static class VersionActivationEndpoint
{
    internal static void MapVersionActivation(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/activate", Activate)
            .RequireAuthorization()
            .Produces<WorkflowVersionActivation>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Fresh owning-type Admin authorization activates one exact immutable version UUID. " +
                "No body or query parameters. Atomically updates only the active workflow-version pointer, audits " +
                "before/after UUIDs and notifies registry listeners. Latest registration baseline, pinned runs, " +
                "schedule configs and fire cursors remain unchanged. Incompatible unpinned schedule variables " +
                "conflict; pinned schedules keep their selected versions. Repeating the active UUID is a no-op.");

    private static async Task<IResult> Activate(HttpContext context, string team, string type, string workflow,
        string version, NpgsqlDataSource source, IAuthorizationService authorization,
        ILogger<WorkflowVersionActivation> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        var workflowId = team + "/" + type + "/" + workflow;
        if (!WorkflowIdentity.TryGetTypeScope(workflowId, out var scope) ||
            !Guid.TryParseExact(version, "D", out var id) || id == Guid.Empty || id.ToString("D") != version ||
            context.Request.QueryString.HasValue ||
            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return Problem(400, "Use an exact workflow version UUID with no request body or query", "invalid_version_activation");
        var token = context.RequestAborted;
        try
        {
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Type, scope, token), TeamAuthorizationExtensions.Policy(RegistryRole.Admin));
            if (!allowed.Succeeded || !Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using (var gate = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended('wf.registry:'||current_schema()||':'||$1,0))",
                connection, transaction))
            {
                gate.Parameters.AddWithValue(team);
                await gate.ExecuteNonQueryAsync(token);
            }
            Guid? previous, latest;
            await using (var read = new NpgsqlCommand("""
                SELECT active_version_id,latest_version_id,deleted_at FROM workflows WHERE id=$1 FOR UPDATE
                """, connection, transaction))
            {
                read.Parameters.AddWithValue(workflowId);
                await using var row = await read.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) return Problem(404, "Workflow not found", "workflow_not_found");
                if (!row.IsDBNull(2)) return Problem(409, "A deleted workflow cannot be activated", "workflow_deleted");
                previous = row.IsDBNull(0) ? null : row.GetGuid(0);
                latest = row.IsDBNull(1) ? null : row.GetGuid(1);
            }
            WorkflowManifest manifest;
            await using (var read = new NpgsqlCommand(
                "SELECT semver,manifest::text FROM workflow_versions WHERE workflow_id=$1 AND id=$2", connection, transaction))
            {
                read.Parameters.AddWithValue(workflowId);
                read.Parameters.AddWithValue(id);
                await using var row = await read.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) return Problem(404, "Workflow version not found", "version_not_found");
                manifest = JsonSerializer.Deserialize(row.GetString(1), WorkflowManifestJsonContext.Default.WorkflowManifest)
                    ?? throw new InvalidDataException("The activation target manifest is null.");
                if (manifest.Name != workflowId || manifest.Version != row.GetString(0))
                    throw new InvalidDataException("The activation target manifest has a different identity.");
            }
            if (previous == id)
                return Results.Json(new WorkflowVersionActivation(workflowId, id, latest),
                    VersionsJsonContext.Default.WorkflowVersionActivation);
            await using (var schedules = new NpgsqlCommand("""
                SELECT id,variables::text FROM schedules
                WHERE workflow_id=$1 AND deleted_at IS NULL AND version_pin IS NULL ORDER BY id
                """, connection, transaction))
            {
                schedules.Parameters.AddWithValue(workflowId);
                await using var rows = await schedules.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token))
                {
                    var values = JsonSerializer.Deserialize(rows.GetString(1),
                        ScheduleFileJsonContext.Default.ImmutableDictionaryStringJsonElement)
                        ?? throw new InvalidDataException("Schedule variables are null.");
                    var validation = VariableBinder.Resolve(manifest.Variables, values, file: "schedule",
                        path: $"$.schedules[\"{rows.GetGuid(0):D}\"].variables", cancellationToken: token);
                    if (!validation.IsValid)
                        return Results.Problem(statusCode: 409, title: "Existing schedules are incompatible with this workflow version",
                            extensions: new Dictionary<string, object?>
                            {
                                ["code"] = "activation_schedule_conflict", ["errors"] = validation.Errors
                            });
                }
            }
            await using (var write = new NpgsqlCommand("""
                WITH updated AS (
                    UPDATE workflows SET active_version_id=$2 WHERE id=$1 RETURNING id
                ), logged AS (
                    INSERT INTO audit_log(actor,actor_kind,action,target_kind,target_id,team_id,summary)
                    SELECT $3,$4,'workflow.activate','Workflow',id,$5,
                        jsonb_build_object('before',jsonb_build_object('activeVersionId',$6::uuid),
                            'after',jsonb_build_object('activeVersionId',$2::uuid),'latestVersionId',$7::uuid)
                    FROM updated RETURNING target_id
                )
                SELECT pg_notify('registry',target_id) FROM logged
                """, connection, transaction))
            {
                write.Parameters.AddWithValue(workflowId);
                write.Parameters.AddWithValue(id);
                write.Parameters.AddWithValue(actor.ToString("D"));
                write.Parameters.AddWithValue(context.User.HasClaim("idtyp", "app") ? "App" : "User");
                write.Parameters.AddWithValue(team);
                write.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)previous ?? DBNull.Value);
                write.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)latest ?? DBNull.Value);
                await write.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            return Results.Json(new WorkflowVersionActivation(workflowId, id, latest),
                VersionsJsonContext.Default.WorkflowVersionActivation);
        }
        catch (NpgsqlException error)
        {
            logger.LogError("Workflow version activation storage is unavailable ({SqlState}).", error.SqlState);
            return Problem(503, "Workflow version data is unavailable", "version_data_unavailable");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or InvalidCastException)
        {
            logger.LogError("Workflow version activation data is inconsistent.");
            return Problem(500, "Workflow version data is inconsistent", "version_data_inconsistent");
        }
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
