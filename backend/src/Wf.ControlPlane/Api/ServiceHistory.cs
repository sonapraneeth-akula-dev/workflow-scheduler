using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Npgsql;
using Wf.ControlPlane.Api.Series;

namespace Wf.ControlPlane.Api;

public sealed record WorkflowHealthDay(DateOnly Day, IReadOnlyDictionary<string, long> Counts);
public sealed record ServiceWorkflow(CatalogWorkflow Workflow, IReadOnlyList<WorkflowHealthDay> Days,
    IReadOnlyList<CatalogLatestRun> Runs);
public sealed record ServiceHistoryPage(string Team, string? Type, string Window, DateOnly From, DateOnly To,
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
            .WithDescription("Authorized, team-scoped workflows with daily counts and the latest 50 run instances per workflow across all series and manual runs. " +
                "window=7d|14d|30d|90d (default 14d) includes the current UTC run day; from inclusive, to exclusive. " +
                "Each day records all named run statuses; no runs means empty counts, not success. " +
                "Run instances share the counts' snapshot and window, ordered by createdAt then id descending. " +
                "Uses the same exact team/type, keyset cursor and metadata filters as the workflow catalogue. No payloads or capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "team", "type", "window", "cursor", "limit", "meta.*", "defaults" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Required = name == "team",
                        Schema = new OpenApiSchema { Type = name == "limit" ? JsonSchemaType.Integer :
                            name == "defaults" ? JsonSchemaType.Boolean : JsonSchemaType.String }
                    });
                return Task.CompletedTask;
            });
    }

    private static async Task<IResult> ReadServiceHistory(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization, TimeProvider time)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        CatalogQuery filter;
        TrendWindow window;
        try
        {
            filter = CatalogQuery.Parse(new QueryCollection(context.Request.Query
                .Where(entry => entry.Key != "window").ToDictionary()));
            var name = context.Request.Query.TryGetValue("window", out var values)
                ? values.Count == 1 ? values[0] : null : "14d";
            var days = name switch
            {
                "7d" => 7, "14d" => 14, "30d" => 30, "90d" => 90,
                _ => throw new ArgumentException("Invalid service history window.")
            };
            var to = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1);
            window = new(name!, to.AddDays(-days), to);
            if (filter.Team is null) throw new ArgumentException("Choose an exact team for service history.");
        }
        catch (ArgumentException error)
        {
            return Results.Problem(statusCode: 400, title: "Invalid service history filter", detail: error.Message);
        }
        try
        {
            var page = await ReadWorkflowPage(context, source, authorization, filter);
            var counts = new Dictionary<(string Workflow, DateOnly Day), Dictionary<string, long>>();
            var runs = new Dictionary<string, List<CatalogLatestRun>>(StringComparer.Ordinal);
            if (page.Items.Count > 0)
            {
                await using var query = source.CreateCommand("""
                    SELECT workflow_id,run_day,status,count(*) AS total,
                        NULL::uuid AS id,NULL::timestamptz AS created_at,
                        NULL::timestamptz AS started_at,NULL::timestamptz AS ended_at
                    FROM runs
                    WHERE workflow_id=ANY($1) AND run_day>=$2 AND run_day<$3
                    GROUP BY workflow_id,run_day,status
                    UNION ALL
                    SELECT r.workflow_id,r.run_day,r.status,NULL::bigint,
                        r.id,r.created_at,r.started_at,r.ended_at
                    FROM unnest($1::text[]) AS w(workflow_id)
                    CROSS JOIN LATERAL (
                        SELECT workflow_id,run_day,status,id,created_at,started_at,ended_at
                        FROM runs
                        WHERE workflow_id=w.workflow_id AND run_day>=$2 AND run_day<$3
                        ORDER BY created_at DESC,id DESC LIMIT 50
                    ) AS r
                    ORDER BY workflow_id,created_at DESC,id DESC
                    """);
                query.Parameters.AddWithValue(page.Items.Select(item => item.Id).ToArray());
                query.Parameters.AddWithValue(window.From);
                query.Parameters.AddWithValue(window.To);
                await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
                while (await reader.ReadAsync(context.RequestAborted))
                {
                    if (!reader.IsDBNull(4))
                    {
                        var workflowId = reader.GetString(0);
                        if (!runs.TryGetValue(workflowId, out var instances))
                            runs.Add(workflowId, instances = []);
                        instances.Add(new(reader.GetGuid(4), Status(reader.GetInt16(2)),
                            reader.GetFieldValue<DateTimeOffset>(5),
                            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
                        continue;
                    }
                    var key = (reader.GetString(0), reader.GetFieldValue<DateOnly>(1));
                    if (!counts.TryGetValue(key, out var statuses)) counts.Add(key, statuses = new(StringComparer.Ordinal));
                    var count = reader.GetInt64(3);
                    if (count <= 0) throw new InvalidDataException("A workflow health count is not positive.");
                    statuses.Add(Status(reader.GetInt16(2)), count);
                }
            }
            var items = page.Items.Select(workflow => new ServiceWorkflow(workflow,
                Enumerable.Range(0, window.Days).Select(offset =>
                {
                    var day = window.From.AddDays(offset);
                    return new WorkflowHealthDay(day, counts.GetValueOrDefault((workflow.Id, day)) ?? []);
                }).ToArray(), runs.GetValueOrDefault(workflow.Id) ?? [])).ToArray();
            return Results.Json(new(filter.Team, filter.Type, window.Name, window.From, window.To, items, page.NextCursor),
                ServiceHistoryJson.Default.ServiceHistoryPage);
        }
        catch (NpgsqlException)
        {
            return Results.Problem(statusCode: 503, title: "Service history is unavailable");
        }
        catch (Exception error) when (error is InvalidDataException or InvalidCastException or OverflowException)
        {
            return Results.Problem(statusCode: 500, title: "Committed service history data is inconsistent");
        }
    }
}
