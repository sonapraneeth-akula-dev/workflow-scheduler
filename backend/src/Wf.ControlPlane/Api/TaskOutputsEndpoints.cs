using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Outputs;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class TaskOutputsEndpoints
{
    public static IEndpointRouteBuilder MapTaskOutputsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/outputs", Read).RequireAuthorization()
            .Produces<TaskOutputsResponse>().Produces(401).ProducesProblem(400).ProducesProblem(403).Produces(404).ProducesProblem(503)
            .WithDescription("Structured JSON outputs of one exact committed task attempt, during or after its run. " +
                "Fresh Run Viewer authorization. No ancestor/source fallback; null means no recorded outputs. " +
                "Task output objects are limited to 65536 UTF-8 bytes; no errors, variables or capabilities are returned.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "taskKey", In = ParameterLocation.Query, Required = true,
                    Description = "Exact stored node/task key; URI-encode as a query value.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "attempt", In = ParameterLocation.Query, Required = true,
                    Description = "Exact attempt number, 1..32767.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer }
                });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!Guid.TryParseExact(id, "D", out var runId) || runId.Version != 7 || id != runId.ToString("D") ||
            context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/runs/{id}/outputs")
            return Results.Problem(statusCode: 400, title: "Invalid run identity");
        try
        {
            if (!(await authorization.AuthorizeAsync(context.User,
                    new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
                    TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            var query = context.Request.Query;
            if (query.Count != 2 || query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("taskKey" or "attempt")) ||
                query["taskKey"].ToString().Split('/') is not [var node, var task] ||
                string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(task) ||
                Encoding.UTF8.GetByteCount(node) > 512 || Encoding.UTF8.GetByteCount(task) > 512 ||
                !short.TryParse(query["attempt"], NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt < 1)
                return Results.Problem(statusCode: 400, title: "Invalid task outputs query");
            await using var command = source.CreateCommand("""
                SELECT outputs::text FROM task_attempts
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                """);
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(query["taskKey"].ToString());
            command.Parameters.AddWithValue(attempt);
            await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
            if (!await reader.ReadAsync(context.RequestAborted)) return Results.NotFound();
            JsonElement? outputs = null;
            if (!reader.IsDBNull(0))
            {
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(0));
                if (TaskOutputJson.Error(bytes) is not null) throw new InvalidDataException();
                using var document = JsonDocument.Parse(bytes);
                outputs = document.RootElement.Clone();
            }
            return Results.Json(new TaskOutputsResponse(runId, query["taskKey"].ToString(), attempt, outputs),
                TaskOutputsJsonContext.Default.TaskOutputsResponse);
        }
        catch (Exception error) when (error is NpgsqlException or JsonException or InvalidDataException or InvalidOperationException)
        { return Results.Problem(statusCode: 503, title: "Task outputs unavailable"); }
    }
}
