using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static partial class RunEventsEndpoints
{
    private static void MapSystemEvents(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/system-events", SystemEvents).RequireAuthorization()
            .Produces<SystemEventsPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Read-only durable system transitions, ascending sequence, at most 200 per page. " +
                "Optional exact node, full node/task and attempt filters. Cursor binds run, filters, limit and committed " +
                "watermark; refresh without a cursor for newer events. Sequence strings preserve Int64 precision. " +
                "Selected agent and reason are actual dispatch facts; plannedAgentId is a pre-decision affinity/run pin, " +
                "not a predicted agent. Missing legacy metadata stays null. Pinned version identifies the declared Task Plan. " +
                "No task logs, variables, outputs, failure text, remote URLs, operation IDs or machine capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "node", "task", "attempt", "limit", "cursor" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Required = false,
                        Description = name switch
                        {
                            "node" => "Exact node key (UTF-8 at most 512 bytes).",
                            "task" => "Exact full node/task key; must agree with node when both are supplied.",
                            "attempt" => "Canonical positive smallint, 1-32767.",
                            "limit" => "Canonical page size 1-200; default 100.",
                            _ => "Opaque cursor from the preceding page; keep run, filters and limit unchanged."
                        },
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                return Task.CompletedTask;
            });
    }

    private static async Task<IResult> SystemEvents(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        Guid run;
        try
        {
            run = RunEventsQuery.RunId(id);
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] !=
                $"/api/v1/runs/{id}/system-events")
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Problem(400, "Invalid run identity", "invalid_run_identity"); }
        try
        {
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            SystemEventsQuery query;
            try
            {
                if (context.Request.QueryString.Value?.Length > 4096) throw new ArgumentException();
                query = SystemEventsQuery.Parse(run, context.Request.Query);
            }
            catch (ArgumentException) { return Problem(400, "Invalid system events query", "invalid_system_events_query"); }
            var token = context.RequestAborted;
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var header = await Header(connection, run, token) ?? await PendingTrigger(connection, run, token);
            if (header is null) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            var through = query.ThroughSeq ?? header.LastSeq;
            if (through > header.LastSeq)
                return Problem(400, "Events cursor exceeds committed history", "invalid_system_events_query");
            var events = new List<SystemEvent>();
            await using (var command = new NpgsqlCommand("""
                SELECT seq,payload FROM run_events
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND seq>$2 AND seq<=$3
                    AND ($4::text IS NULL OR kind=1 AND entity_key=$4 OR kind=2 AND split_part(entity_key,'/',1)=$4)
                    AND ($5::text IS NULL OR kind=2 AND entity_key=$5)
                    AND ($6::smallint IS NULL OR kind=2 AND attempt=$6)
                ORDER BY seq LIMIT $7
                """, connection))
            {
                command.Parameters.AddWithValue(run);
                command.Parameters.AddWithValue(query.AfterSeq);
                command.Parameters.AddWithValue(through);
                command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)query.NodeKey ?? DBNull.Value });
                command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)query.TaskKey ?? DBNull.Value });
                command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Smallint, Value = query.Attempt is { } n ? (short)n : DBNull.Value });
                command.Parameters.AddWithValue(query.Limit + 1);
                await using var reader = await command.ExecuteReaderAsync(token);
                var previous = query.AfterSeq;
                while (await reader.ReadAsync(token))
                {
                    var sequence = reader.GetInt64(0);
                    if (sequence <= previous || sequence > through) throw new InvalidDataException();
                    var change = Event(reader, run, sequence);
                    if (query.NodeKey is { } node && !(change.Entity == Wf.Contracts.Events.EventEntity.Node && change.Key == node ||
                            change.Entity == Wf.Contracts.Events.EventEntity.Task && change.Key?.Split('/')[0] == node) ||
                        query.TaskKey is { } task && (change.Entity != Wf.Contracts.Events.EventEntity.Task || change.Key != task) ||
                        query.Attempt is { } attempt && change.Attempt != attempt)
                        throw new InvalidDataException("Committed event columns disagree with the filtered payload.");
                    events.Add(SystemEvent.From(change));
                    previous = sequence;
                }
            }
            var more = events.Count > query.Limit;
            if (more) events.RemoveAt(events.Count - 1);
            var next = more ? query.Cursor(run, long.Parse(events[^1].Seq, CultureInfo.InvariantCulture), through) : null;
            await transaction.CommitAsync(token);
            return Results.Json(new SystemEventsPage(run, header.WorkflowId, header.VersionId,
                through.ToString(CultureInfo.InvariantCulture), events.ToArray(), next), SystemEventsJson.Default.SystemEventsPage);
        }
        catch (NpgsqlException) { return Problem(503, "Committed run data is unavailable", "run_data_unavailable"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException)
        { return Problem(500, "Committed run data is inconsistent", "run_data_inconsistent"); }
    }
}
