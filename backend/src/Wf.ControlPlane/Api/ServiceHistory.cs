using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Npgsql;
using Wf.ControlPlane.Api.Series;

namespace Wf.ControlPlane.Api;

public sealed record WorkflowHealthDay(DateOnly Day, IReadOnlyDictionary<string, long> Counts);
public sealed record ServiceWorkflow(CatalogWorkflow Workflow, IReadOnlyList<WorkflowHealthDay> Days);
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
            .WithDescription("Authorized, team-scoped workflow cards and daily run counts across all series and manual runs. " +
                "window=7d|30d|90d includes the current UTC run day; from inclusive, to exclusive. " +
                "Each day records all named run statuses; no runs means empty counts, not success. " +
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
            window = TrendWindow.Parse(new QueryCollection(context.Request.Query
                .Where(entry => entry.Key == "window").ToDictionary()), time.GetUtcNow());
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
            if (page.Items.Count > 0)
            {
                await using var query = source.CreateCommand("""
                    SELECT workflow_id,run_day,status,count(*) FROM runs
                    WHERE workflow_id=ANY($1) AND run_day>=$2 AND run_day<$3
                    GROUP BY workflow_id,run_day,status
                    """);
                query.Parameters.AddWithValue(page.Items.Select(item => item.Id).ToArray());
                query.Parameters.AddWithValue(window.From);
                query.Parameters.AddWithValue(window.To);
                await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
                while (await reader.ReadAsync(context.RequestAborted))
                {
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
                }).ToArray())).ToArray();
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
