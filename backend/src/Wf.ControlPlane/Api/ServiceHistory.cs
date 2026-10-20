using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Schedules;

namespace Wf.ControlPlane.Api;

public sealed record ServiceWorkflow(CatalogWorkflow Workflow, IReadOnlyList<CatalogLatestRun> Runs)
{
    public RunHistorySchedule[] Schedules { get; init; } = [];
    public ServiceHistoryCard[]? HistoryCards { get; init; }
}
public sealed record ServiceHistoryCard(string Id, string Name, Guid[] SeriesIds);
public sealed record ServiceHistoryPage(string Team, string? Type, int RunLimit,
    IReadOnlyList<ServiceWorkflow> Items, string? NextCursor);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceHistoryPage))]
public partial class ServiceHistoryJson : JsonSerializerContext;

public static partial class CatalogEndpoints
{
    private static void MapServiceHistory(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/service-history", ReadServiceHistory).RequireAuthorization()
            .Produces<ServiceHistoryPage>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Authorized, team-scoped workflows with their latest run instances across all series and manual runs, without a date cutoff. " +
                "runLimit=1..30 (default 10) applies separately to each workflow; fewer runs means fewer instances, never fabricated outcomes. " +
                "Run instances are ordered by createdAt then id descending. " +
                "Each item also contains authorized schedule names and permanent series identities, including paused/deleted schedules for retained history tracking. " +
                "historyCards comes from the active applied schedules.json snapshot: null preserves one all-runs card; [] hides the workflow; otherwise ordered visible cards select permanent series identities, including schedule-based manual runs. " +
                "Uses the same exact team/type, keyset cursor and metadata filters as the workflow catalogue. No payloads or capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "team", "type", "runLimit", "cursor", "limit", "meta.*", "defaults" })
                {
                    var schema = new OpenApiSchema { Type = name is "limit" or "runLimit" ? JsonSchemaType.Integer :
                        name == "defaults" ? JsonSchemaType.Boolean : JsonSchemaType.String };
                    if (name == "runLimit") { schema.Minimum = "1"; schema.Maximum = "30"; schema.Default = JsonValue.Create(10); }
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Required = name == "team",
                        Schema = schema
                    });
                }
                return Task.CompletedTask;
            });
    }

    private static async Task<IResult> ReadServiceHistory(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        CatalogQuery filter;
        var runLimit = 10;
        try
        {
            filter = CatalogQuery.Parse(new QueryCollection(context.Request.Query
                .Where(entry => entry.Key != "runLimit").ToDictionary()));
            if (context.Request.Query.TryGetValue("runLimit", out var values) &&
                (values.Count != 1 ||
                 !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out runLimit) ||
                 runLimit is < 1 or > 30))
                throw new ArgumentException("The run limit must be a whole number from 1 to 30.");
            if (filter.Team is null) throw new ArgumentException("Choose an exact team for service history.");
        }
        catch (ArgumentException error)
        {
            return Results.Problem(statusCode: 400, title: "Invalid service history filter", detail: error.Message);
        }
        try
        {
            var page = await ReadWorkflowPage(context, source, authorization, filter, includeFailures: false);
            var runs = new Dictionary<string, List<CatalogLatestRun>>(StringComparer.Ordinal);
            if (page.Items.Count > 0)
            {
                await using var query = source.CreateCommand("""
                    SELECT r.workflow_id,r.id,r.status,r.created_at,r.started_at,r.ended_at
                    FROM unnest($1::text[]) AS w(workflow_id)
                    CROSS JOIN LATERAL (
                        SELECT workflow_id,status,id,created_at,started_at,ended_at
                        FROM runs
                        WHERE workflow_id=w.workflow_id
                        ORDER BY created_at DESC,id DESC LIMIT $2
                    ) AS r
                    ORDER BY workflow_id,created_at DESC,id DESC
                    """);
                query.Parameters.AddWithValue(page.Items.Select(item => item.Id).ToArray());
                query.Parameters.AddWithValue(runLimit);
                await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
                while (await reader.ReadAsync(context.RequestAborted))
                {
                    var workflowId = reader.GetString(0);
                    if (!runs.TryGetValue(workflowId, out var instances))
                        runs.Add(workflowId, instances = []);
                    instances.Add(new(reader.GetGuid(1), Status(reader.GetInt16(2)),
                        reader.GetFieldValue<DateTimeOffset>(3),
                        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
                }
            }
            var failures = await RunFailures.Summaries(context, source, runs.Values.SelectMany(instances => instances).Select(run => run.Id));
            var schedules = await RunHistoryEndpoints.ReadSchedules(source, page.Items.Select(item => item.Id).ToArray(), context.RequestAborted);
            var cards = await ReadHistoryCards(source, page.Items.Select(item => item.Id).ToArray(), context.RequestAborted);
            var items = page.Items.Select(workflow => new ServiceWorkflow(
                workflow.LatestRun is not { } latest ? workflow : workflow with
                {
                    LatestRun = latest with { FailureSummary = failures.GetValueOrDefault(latest.Id) }
                },
                (runs.GetValueOrDefault(workflow.Id) ?? []).Select(run =>
                    run with { FailureSummary = failures.GetValueOrDefault(run.Id) }).ToArray())
                {
                    Schedules = schedules.GetValueOrDefault(workflow.Id)?.ToArray() ?? [],
                    HistoryCards = cards.GetValueOrDefault(workflow.Id)
                }).ToArray();
            return Results.Json(new(filter.Team, filter.Type, runLimit, items, page.NextCursor),
                ServiceHistoryJson.Default.ServiceHistoryPage);
        }
        catch (NpgsqlException)
        {
            return Results.Problem(statusCode: 503, title: "Service history is unavailable");
        }
        catch (Exception error) when (error is InvalidDataException or InvalidCastException or OverflowException or JsonException)
        {
            return Results.Problem(statusCode: 500, title: "Committed service history data is inconsistent");
        }
    }

    private static async Task<Dictionary<string, ServiceHistoryCard[]>> ReadHistoryCards(
        NpgsqlDataSource source, string[] workflows, CancellationToken token)
    {
        var result = new Dictionary<string, ServiceHistoryCard[]>(StringComparer.Ordinal);
        if (workflows.Length == 0) return result;
        var files = new List<(string Workflow, ScheduleFile File)>();
        await using (var query = source.CreateCommand("""
            SELECT w.id,c.content FROM workflows w
            JOIN schedule_config_versions c ON c.id=w.active_schedule_config_id AND c.workflow_id=w.id
            WHERE w.id=ANY($1)
            """))
        {
            query.Parameters.AddWithValue(workflows);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(1))
                    throw new InvalidDataException("The active schedule configuration has no retained content.");
                var file = JsonSerializer.Deserialize(reader.GetString(1), ScheduleFileJsonContext.Default.ScheduleFile)
                    ?? throw new InvalidDataException("The active schedule configuration is null.");
                if (file.HistoryCards is not null) files.Add((reader.GetString(0), file));
            }
        }
        if (files.Count == 0) return result;
        var identities = new Dictionary<(string Workflow, string Key), Guid>();
        await using (var query = source.CreateCommand("""
            SELECT s.workflow_id,s.code_key,r.id FROM schedules s
            JOIN run_series r ON r.schedule_id=s.id AND r.workflow_id=s.workflow_id
            WHERE s.workflow_id=ANY($1) AND s.origin=0
            """))
        {
            query.Parameters.AddWithValue(files.Select(file => file.Workflow).ToArray());
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                identities.Add((reader.GetString(0), reader.GetString(1)), reader.GetGuid(2));
        }
        foreach (var (workflow, file) in files)
            result.Add(workflow, file.HistoryCards!.Value.Where(card => card.Visible).Select(card =>
                new ServiceHistoryCard(card.Id, card.Name, card.ScheduleIds.Select(key =>
                    identities.TryGetValue((workflow, key), out var series) ? series :
                        throw new InvalidDataException("A configured history card has no permanent schedule series."))
                    .ToArray())).ToArray());
        return result;
    }
}
