using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.Contracts.Resources;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine;

namespace Wf.ControlPlane.Api;

public static class ResourceMetricsEndpoints
{
    public static IEndpointRouteBuilder MapResourceMetricsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/resource-metrics", Summary).RequireAuthorization()
            .Produces<ResourceAttemptSummary[]>().Produces(401).ProducesProblem(400).ProducesProblem(403).ProducesProblem(503);
        endpoints.MapGet("/api/v1/runs/{id}/resources", Series).RequireAuthorization()
            .Produces<ResourceSeriesPage>().Produces(401).ProducesProblem(400).ProducesProblem(403).Produces(404).ProducesProblem(503)
            .WithDescription("Fresh run Viewer authorization; newest-first, integrity-verified resource samples. " +
                "taskKey=node/task, attempt=1..32767, limit=1..120 (default 60), optional opaque cursor; nulls retain unavailable reasons.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var (name, required, type, description) in new[]
                {
                    ("taskKey", true, JsonSchemaType.String, "Exact stored node/task key; URI-encode as a query value."),
                    ("attempt", true, JsonSchemaType.Integer, "Attempt number, 1..32767."),
                    ("limit", false, JsonSchemaType.Integer, "Page size, 1..120; default 60."),
                    ("cursor", false, JsonSchemaType.String, "Opaque nextCursor from this attempt; expires after one hour.")
                })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Required = required, Description = description,
                        Schema = new OpenApiSchema { Type = type }
                    });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static Task<IResult> Summary(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, ILogger<ResourceSampleSink> logger) =>
        Read(context, id, authorization, logger, async runId =>
        {
            if (context.Request.Query.Count != 0) throw new ArgumentException("No summary query parameters are supported.");
            await using var query = source.CreateCommand("""
                SELECT task_key,attempt,agent_id,resource_sample_count,resource_avg_cpu_percent,resource_max_cpu_percent,
                       resource_peak_ram_bytes,resource_network_sent_bytes,resource_network_received_bytes,
                       resource_cpu_observed_seconds,resource_network_observed_seconds
                FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 ORDER BY task_key,attempt
                """);
            query.Parameters.AddWithValue(runId);
            await using var row = await query.ExecuteReaderAsync(context.RequestAborted);
            var values = new List<ResourceAttemptSummary>();
            while (await row.ReadAsync(context.RequestAborted))
                values.Add(new(row.GetString(0), row.GetInt16(1), row.IsDBNull(2) ? null : row.GetGuid(2), row.GetInt64(3),
                    row.IsDBNull(4) ? null : row.GetDouble(4), row.IsDBNull(5) ? null : row.GetDouble(5),
                    row.IsDBNull(6) ? null : row.GetDecimal(6), row.IsDBNull(7) ? null : row.GetDecimal(7),
                    row.IsDBNull(8) ? null : row.GetDecimal(8), row.GetDouble(9), row.GetDouble(10),
                    row.IsDBNull(4) ? "No valid CPU duration observed." : null,
                    row.IsDBNull(6) ? "No memory observation available." : null,
                    row.IsDBNull(7) ? "No valid task network counter interval observed; inspect sample reasons." : null));
            return Results.Json(values.ToArray(), ResourceJsonContext.Default.ResourceAttemptSummaryArray);
        });

    private static Task<IResult> Series(HttpContext context, string id, NpgsqlDataSource source, ResourceSampleSink sink,
        ResourceReadCursors cursors, IAuthorizationService authorization, ILogger<ResourceSampleSink> logger) =>
        Read(context, id, authorization, logger, async runId =>
        {
            var query = context.Request.Query;
            if (query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("taskKey" or "attempt" or "limit" or "cursor")) ||
                !query.TryGetValue("taskKey", out var key) || key.ToString().Split('/') is not [var node, var task] ||
                string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(task) || Encoding.UTF8.GetByteCount(key.ToString()) > 1025 ||
                !int.TryParse(query["attempt"], NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt is < 1 or > 32767)
                throw new ArgumentException("Invalid resource series query.");
            var limit = 60;
            if (query.TryGetValue("limit", out var size) &&
                (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 120))
                throw new ArgumentException("Invalid resource page size.");
            ResourcePointer? pointer;
            // Even a signed cursor cannot resurrect a deleted attempt or bypass fresh ownership authorization.
            await using (var lookup = source.CreateCommand("""
                SELECT resource_series_uri,resource_series_sha256,resource_series_bytes FROM task_attempts
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                """))
            {
                lookup.Parameters.AddWithValue(runId);
                lookup.Parameters.AddWithValue(key.ToString());
                lookup.Parameters.AddWithValue((short)attempt);
                await using var row = await lookup.ExecuteReaderAsync(context.RequestAborted);
                if (!await row.ReadAsync(context.RequestAborted)) return Results.NotFound();
                pointer = row.IsDBNull(0) ? null : new(row.GetString(0), row.GetString(1), row.GetInt64(2));
            }
            var scope = $"{runId:D}|{ResourceSampleSink.TaskHash(key.ToString())}|{attempt}";
            if (query.TryGetValue("cursor", out var cursor))
                pointer = cursors.Read(scope, cursor.ToString());
            var samples = new List<ResourceSeriesSample>();
            while (pointer is not null && samples.Count < limit)
            {
                if (!pointer.Key.StartsWith($"resources/{runId:D}/{ResourceSampleSink.TaskHash(key.ToString())}/{attempt}/", StringComparison.Ordinal))
                    throw new InvalidDataException("Resource chain crossed its attempt.");
                var chunk = await sink.ReadAsync(pointer, context.RequestAborted);
                samples.Add(new(chunk.Sequence, chunk.Observation));
                pointer = chunk.Previous;
            }
            return Results.Json(new ResourceSeriesPage(samples.ToArray(), pointer is null ? null : cursors.Create(scope, pointer)),
                ResourceJsonContext.Default.ResourceSeriesPage);
        });

    private static async Task<IResult> Read(HttpContext context, string id, IAuthorizationService authorization,
        ILogger logger, Func<Guid, Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!Guid.TryParseExact(id, "D", out var runId) || runId.Version != 7 || id != runId.ToString("D"))
            return Results.Problem(statusCode: 400, title: "Invalid run ID");
        try
        {
            if (!(await authorization.AuthorizeAsync(context.User, new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
                    TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            return await action(runId);
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid resource metrics query"); }
        catch (Exception error) when (error is NpgsqlException or IOException or InvalidDataException or JsonException or
            InvalidOperationException or RequestFailedException)
        {
            logger.LogError(error, "Resource metrics unavailable.");
            return Results.Problem(statusCode: 503, title: "Resource metrics storage unavailable");
        }
    }
}

public sealed class ResourceReadCursors(byte[] key, TimeProvider time) : IDisposable
{
    private readonly byte[] secret = key.Length >= 32 ? key.ToArray() : throw new ArgumentException("Cursor key requires 256 bits.");
    public string Create(string scope, ResourcePointer pointer)
    {
        var payload = Encoding.UTF8.GetBytes(scope + "|" + time.GetUtcNow().AddHours(1).UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" +
            JsonSerializer.Serialize(pointer, ResourceJsonContext.Default.ResourcePointer));
        return WebEncoders.Base64UrlEncode(payload) + "." + WebEncoders.Base64UrlEncode(HMACSHA256.HashData(secret, payload));
    }
    public ResourcePointer Read(string scope, string token)
    {
        try
        {
            if (token.Length > 2048 || token.Split('.') is not [var data, var signature]) throw new FormatException();
            var payload = WebEncoders.Base64UrlDecode(data);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(secret, payload), WebEncoders.Base64UrlDecode(signature)))
                throw new FormatException();
            var text = Encoding.UTF8.GetString(payload);
            if (!text.StartsWith(scope + "|", StringComparison.Ordinal) ||
                text[(scope.Length + 1)..].Split('|') is not [var expiry, var json] ||
                !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= time.GetUtcNow().UtcTicks)
                throw new FormatException();
            return JsonSerializer.Deserialize(json, ResourceJsonContext.Default.ResourcePointer) ?? throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new ArgumentException("Invalid resource cursor.", error); }
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(secret);
}
