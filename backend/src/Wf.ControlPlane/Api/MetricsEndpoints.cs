using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using Wf.Engine.Core.Metrics;

namespace Wf.ControlPlane.Api;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/metrics", Read).RequireAuthorization()
            .Produces<RunMetricsResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Committed run, lifecycle-stage, node, task and attempt timings, in milliseconds. " +
                "Stages use their reserved keys; nodes excludes stages. Active durations are recorded values, not live clocks. " +
                "Task duration sums execution only; retries count redispatches. Failure categories preserve known producer " +
                "metadata; absent legacy/unknown categories are null. Never-started/skipped/reused work has no fabricated attempts. " +
                "Fresh Run Viewer authorization; no query parameters, variables, errors, outputs or storage capabilities.");
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
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/runs/{id}/metrics")
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
            RunSnapshot snapshot;
            await using (var header = new NpgsqlCommand("""
                SELECT status,last_seq,snapshot_seq FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1
                """, connection))
            {
                header.Parameters.AddWithValue(runId);
                await using var reader = await header.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    var status = reader.GetInt16(0);
                    var last = reader.GetInt64(1);
                    var saved = reader.GetInt64(2);
                    await reader.DisposeAsync();
                    snapshot = (await store.LoadAsync(connection, runId, false, token)).Snapshot;
                    if (saved < 0 || saved > last || snapshot.RunId != runId ||
                        snapshot.LastSequence != last || (int)snapshot.Status != status)
                        throw new InvalidDataException();
                }
                else
                {
                    await reader.DisposeAsync();
                    snapshot = await Pending(connection, runId, token);
                }
            }
            var response = RunMetricsResponse.From(snapshot);
            await transaction.CommitAsync(token);
            return Results.Json(response, MetricsJsonContext.Default.RunMetricsResponse);
        }
        catch (NpgsqlException) { return Problem(503, "Committed run data is unavailable", "run_data_unavailable"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException or KeyNotFoundException or NullReferenceException)
        { return Problem(500, "Committed run data is inconsistent", "run_data_inconsistent"); }
    }

    private static async Task<RunSnapshot> Pending(NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT payload::text,created_at FROM commands WHERE run_id=$1 AND kind=0 AND processed_at IS NULL
            """, connection);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException();
        var trigger = JsonSerializer.Deserialize(reader.GetString(0), RecoveryJson.Default.TriggerRunCommand)
            ?? throw new InvalidDataException();
        CommandInbox.Validate(trigger);
        var created = reader.GetFieldValue<DateTimeOffset>(1);
        if (trigger.InitialState.RunId != id || trigger.DeadlineAt <= created ||
            !WorkflowIdentity.TryGetTypeScope(trigger.WorkflowId, out _))
            throw new InvalidDataException();
        var snapshot = trigger.InitialState;
        if (snapshot.Metrics is not null) _ = RunMetricsResponse.From(snapshot);
        return RunState.Restore(snapshot with { Metrics = RunMetricsRecorder.Initialize(snapshot, created) }, [], _ => { }).Snapshot;
    }

    private static IResult Problem(int status, string title, string reason) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
