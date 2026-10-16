using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;

namespace Wf.ControlPlane.Api;

public static class RunTimelineEndpoints
{
    public static IEndpointRouteBuilder MapRunTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/timeline", Read).RequireAuthorization()
            .WithMetadata(new ResponseCacheAttribute { NoStore = true })
            .Produces<RunTimelineResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("One row per committed dispatched attempt, including reserved lifecycle-stage keys. " +
                "Manifest task declaration order, then increasing attempt; absent manifest uses snapshot task enumeration. " +
                "Timings reuse validated recorded metrics, not live clocks. Agent identity belongs to the exact durable " +
                "dispatch attempt; missing legacy attribution is null. Never-started/skipped/reused work has no invented attempts. " +
                "Fresh Run Viewer authorization; no query parameters, variables, errors, outputs or machine capabilities.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, RunStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        Guid runId;
        try
        {
            runId = RunEventsQuery.RunId(id);
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/runs/{id}/timeline")
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Problem(400, "Invalid run identity", "invalid_run_identity"); }
        try
        {
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            if (context.Request.QueryString.HasValue)
                return Problem(400, "Invalid run query", "invalid_run_query");
            var token = context.RequestAborted;
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var snapshot = await CommittedRunRead.Load(connection, store, runId, token);
            var agents = await Agents(connection, runId, token);
            var order = await TaskOrder(connection, runId, token);
            var response = RunTimelineResponse.From(snapshot, agents, order);
            await transaction.CommitAsync(token);
            return Results.Json(response, RunTimelineJsonContext.Default.RunTimelineResponse);
        }
        catch (NpgsqlException) { return Problem(503, "Committed run data is unavailable", "run_data_unavailable"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException or KeyNotFoundException or NullReferenceException)
        { return Problem(500, "Committed run data is inconsistent", "run_data_inconsistent"); }
    }

    private static async Task<Dictionary<(string TaskKey, int Attempt), string?>> Agents(
        NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT task_key,attempt,agent_id FROM task_attempts
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
            """, connection);
        command.Parameters.AddWithValue(id);
        var agents = new Dictionary<(string, int), string?>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            agents.Add((reader.GetString(0), reader.GetInt16(1)),
                reader.IsDBNull(2) ? null : reader.GetGuid(2).ToString("D"));
        return agents;
    }

    private static async Task<IReadOnlyList<string>?> TaskOrder(NpgsqlConnection connection, Guid id,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT (v.manifest->'nodes')::text FROM runs r
            JOIN workflow_versions v ON v.workflow_id=r.workflow_id AND v.id=r.version_id
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1
            """, connection);
        command.Parameters.AddWithValue(id);
        if (await command.ExecuteScalarAsync(token) is not string nodes) return null;
        using var document = JsonDocument.Parse(nodes);
        return document.RootElement.EnumerateArray().SelectMany(node =>
            node.GetProperty("tasks").EnumerateArray().Select(task =>
                node.GetProperty("key").GetString() + "/" + task.GetProperty("key").GetString())).ToArray();
    }

    private static IResult Problem(int status, string title, string reason) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
