using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core.Placement;

namespace Wf.ControlPlane.Api;

public sealed record ReservationQueueItem(string NodeKey, int Position, int Count, DateTimeOffset QueuedAt,
    Guid? BlockingReservationId, Guid? BlockingRunId);
public sealed record ReservationQueueResponse(Guid RunId, IReadOnlyList<ReservationQueueItem> Items);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReservationQueueResponse))]
public partial class ReservationQueueJsonContext : JsonSerializerContext;

public static class ReservationQueueEndpoints
{
    public static IEndpointRouteBuilder MapReservationQueueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/reservation-queue", Read).RequireAuthorization()
            .Produces<ReservationQueueResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Current one-based global FIFO position of each waiting node. Fresh Run Viewer authorization. " +
                "Includes one matching unreleased blocking reservation and its run only when that run is also currently visible. " +
                "Ended/aborted owners are excluded immediately; empty items means no live reservation wait. " +
                "No selectors, variables, machine labels or capabilities. Independent of run-event sequence; poll for changes.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        Guid runId;
        try
        {
            runId = RunEventsQuery.RunId(id);
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget != $"/api/v1/runs/{id}/reservation-queue")
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid reservation queue request"); }
        var token = context.RequestAborted;
        try
        {
            if (!await Allowed(runId)) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var queued = new List<(string Node, int Position, int Count, DateTimeOffset At, PlacementSelector Selector)>();
            await using (var query = new NpgsqlCommand("""
                WITH live_queue AS (
                    SELECT q.*,row_number() OVER(ORDER BY q.position) AS current_position
                    FROM reservation_queue q JOIN runs r ON r.run_day=q.run_day AND r.id=q.run_id
                    JOIN node_runs n ON n.run_day=q.run_day AND n.run_id=q.run_id AND n.node_key=q.node_key
                    WHERE r.status IN (0,1) AND n.status BETWEEN 0 AND 4
                )
                SELECT node_key,current_position,machine_count,queued_at,selector::text
                FROM live_queue WHERE run_id=$1 ORDER BY position
                """, connection))
            {
                query.Parameters.AddWithValue(runId);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    queued.Add((reader.GetString(0), checked((int)reader.GetInt64(1)), reader.GetInt32(2),
                        reader.GetFieldValue<DateTimeOffset>(3),
                        JsonSerializer.Deserialize(reader.GetString(4), ReservationJson.Default.PlacementSelector)
                            ?? throw new InvalidDataException("Queued reservation selector is null.")));
            }
            var blockers = new List<(Guid Reservation, Guid Run, PlacementAgent Agent)>();
            if (queued.Count > 0)
            {
                await using var query = new NpgsqlCommand("""
                    SELECT r.id,r.run_id,a.id,a.name,a.os,a.pool,a.max_tasks,a.ram_mb,a.labels
                    FROM reservations r JOIN agents a ON a.id=r.agent_id
                    WHERE r.released_at IS NULL ORDER BY r.created_at,r.id
                    """, connection);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var labels = reader.GetFieldValue<string[]>(8).Select(label => label.Split('=', 2))
                        .ToImmutableDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
                    blockers.Add((reader.GetGuid(0), reader.GetGuid(1), new(reader.GetGuid(2), reader.GetString(3),
                        reader.GetInt16(4) == 0 ? "windows" : "linux", reader.GetString(5), 0,
                        reader.GetInt32(6), reader.GetInt32(7), Labels: labels)));
                }
            }
            await transaction.CommitAsync(token);
            var items = new List<ReservationQueueItem>();
            var visible = new Dictionary<Guid, bool>();
            foreach (var row in queued)
            {
                PlacementFilter.EnsureResolved(row.Selector);
                var blocker = blockers.Find(candidate => PlacementFilter.Matches(row.Selector, candidate.Agent));
                var allowed = false;
                if (blocker.Reservation != Guid.Empty)
                {
                    if (!visible.TryGetValue(blocker.Run, out allowed))
                        visible.Add(blocker.Run, allowed = await Allowed(blocker.Run));
                }
                items.Add(new(row.Node, row.Position, row.Count, row.At,
                    allowed ? blocker.Reservation : null, allowed ? blocker.Run : null));
            }
            return Results.Json(new ReservationQueueResponse(runId, items),
                ReservationQueueJsonContext.Default.ReservationQueueResponse);
        }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Reservation queue unavailable"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException or IndexOutOfRangeException)
        { return Results.Problem(statusCode: 500, title: "Committed reservation queue data is inconsistent"); }

        async Task<bool> Allowed(Guid run) =>
            (await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, run.ToString("D"), token),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded;
    }
}
