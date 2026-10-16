using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Registry;
using Wf.Contracts.Reports;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class ReportIndexEndpoints
{
    public static IEndpointRouteBuilder MapReportIndexEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/reports", Read).RequireAuthorization()
            .Produces<ReportIndexResponse>().ProducesProblem(400).Produces(401).ProducesProblem(403).ProducesProblem(503)
            .WithDescription("Fresh Run Viewer metadata only. Optional kind/tag filters are single, exact, " +
                "case-sensitive, nonblank values of at most 128 UTF-8 bytes. Both filters combine with AND. " +
                "Stable createdAt/id order; sourceKey is the exact historical stage/node/task key, attempt is real, " +
                "createdAt is committed UTC. No payload, variables, hash, blob path or download capability. " +
                "On run Resync or SignalR reconnect, refetch this index; ReportAdded is an invalidation, not a run event.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "kind", "tag" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Required = false,
                        Description = "Single exact nonblank value; at most 128 UTF-8 bytes.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    public static (string? Kind, string? Tag) Filters(IQueryCollection query)
    {
        if (query.Keys.Any(key => key is not ("kind" or "tag"))) throw new ArgumentException("Unsupported report filter.");
        return (Value("kind"), Value("tag"));

        string? Value(string name)
        {
            if (!query.TryGetValue(name, out var values)) return null;
            if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) throw new ArgumentException("Invalid report filter.");
            var value = values[0]!;
            if (value.Length > 128 || new UTF8Encoding(false, true).GetByteCount(value) > 128 || value.Contains('\0'))
                throw new ArgumentException("Invalid report filter.");
            return value;
        }
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, ILoggerFactory logging)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        Guid runId;
        try
        {
            runId = RunEventsQuery.RunId(id);
            if (id != runId.ToString("D") ||
                context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/runs/{id}/reports")
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid run identity"); }
        try
        {
            var token = context.RequestAborted;
            var permitted = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, id, token),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!permitted.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            (string? Kind, string? Tag) filters;
            try { filters = Filters(context.Request.Query); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid report filters"); }
            await using var query = source.CreateCommand("""
                SELECT id,source_key,attempt,kind,title,tags,size_bytes,created_at FROM reports
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                  AND ($2::text IS NULL OR kind=$2) AND ($3::text IS NULL OR tags @> ARRAY[$3]::text[])
                ORDER BY created_at,id
                """);
            query.Parameters.AddWithValue(runId);
            query.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filters.Kind ?? DBNull.Value);
            query.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filters.Tag ?? DBNull.Value);
            var items = new List<ReportIndexItem>();
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                items.Add(new()
                {
                    Id = reader.GetGuid(0), RunId = runId, SourceKey = reader.GetString(1), Attempt = reader.GetInt16(2),
                    Kind = reader.GetString(3), Title = reader.GetString(4), Tags = reader.GetFieldValue<string[]>(5),
                    SizeBytes = reader.GetInt64(6), CreatedAt = reader.GetFieldValue<DateTimeOffset>(7)
                });
            return Results.Json(new ReportIndexResponse { RunId = runId, Items = items.ToArray() },
                ReportIndexJsonContext.Default.ReportIndexResponse);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidDataException or InvalidOperationException)
        {
            logging.CreateLogger("Wf.ReportIndex").LogError(error, "Report metadata index unavailable.");
            return Results.Problem(statusCode: 503, title: "Report index unavailable");
        }
    }
}
