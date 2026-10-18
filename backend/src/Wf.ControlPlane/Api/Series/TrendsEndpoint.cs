using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Series;

public sealed record TrendMetrics(long Runs, long Succeeded, long Failed, long DurationCount,
    double? SuccessRate, double? P50Ms, double? P95Ms);
public sealed record SeriesTrendDay(DateOnly Day, TrendMetrics Metrics);
public sealed record SeriesTrendStreak(string? Outcome, long Runs);
public sealed record SeriesTrendsResponse(Guid SeriesId, string Window, DateOnly From, DateOnly To,
    TrendMetrics Totals, SeriesTrendStreak Streak, ImmutableArray<SeriesTrendDay> Days);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SeriesTrendsResponse))]
public partial class SeriesTrendsJson : JsonSerializerContext;

public sealed record TrendWindow(string Name, DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber;

    public static TrendWindow Parse(IQueryCollection query, DateTimeOffset now)
    {
        if (query.Keys.Any(key => key != "window") ||
            query.TryGetValue("window", out var values) && values.Count != 1)
            throw new ArgumentException("Only one window=7d, 30d or 90d is supported.");
        var name = query.TryGetValue("window", out values) ? values[0] : "30d";
        var days = name switch { "7d" => 7, "30d" => 30, "90d" => 90, _ => throw new ArgumentException("Invalid trend window.") };
        var to = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        return new(name!, to.AddDays(-days), to);
    }
}

public static class TrendsEndpoint
{
    public static IEndpointRouteBuilder MapTrendsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/series/{id}/trends", Read).RequireAuthorization()
            .Produces<SeriesTrendsResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Fresh Series Viewer authorization. window=7d|30d|90d (default 30d), including the current " +
                "UTC creation day; from inclusive, to exclusive. Completed runs only; failed includes failed, aborted " +
                "and timed-out. Success rate is a 0..1 fraction, null without completed runs. Exact interpolated " +
                "source duration quantiles in milliseconds, never averaged daily quantiles; missing start/end is " +
                "unobserved, skewed intervals clamp to zero. Daily gaps have zero counts and null measurements. " +
                "Streak is the latest contiguous success/failure sequence in this window ordered by UUIDv7 creation, " +
                "ignoring active runs and skipped/missed fires; null outcome and zero runs for an empty window. " +
                "Retained runs survive schedule deletion. No variables, metadata, errors or storage capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "window", In = ParameterLocation.Query,
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String, Default = JsonValue.Create("30d"),
                        Enum = new[] { "7d", "30d", "90d" }.Select(value => (JsonNode)JsonValue.Create(value)!).ToList()
                    }
                });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, TimeProvider time)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!Guid.TryParseExact(id, "D", out var series) || series == Guid.Empty ||
            context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/series/{id}/trends")
            return Problem(400, "Invalid series identity", "invalid_series_identity");
        try
        {
            var allowed = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Series, id, context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            TrendWindow window;
            try { window = TrendWindow.Parse(context.Request.Query, time.GetUtcNow()); }
            catch (ArgumentException) { return Problem(400, "Invalid trend window", "invalid_trend_window"); }
            var response = await Query(source, series, window, context.RequestAborted);
            return Results.Json(response, SeriesTrendsJson.Default.SeriesTrendsResponse);
        }
        catch (NpgsqlException) { return Problem(503, "Series trends are unavailable", "series_trends_unavailable"); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException or InvalidCastException)
        { return Problem(500, "Committed trend data is inconsistent", "series_trends_inconsistent"); }
    }

    private static async Task<SeriesTrendsResponse> Query(NpgsqlDataSource source, Guid series, TrendWindow window,
        CancellationToken token)
    {
        await using var query = source.CreateCommand("""
            WITH completed AS (
                SELECT run_day,status,
                    CASE WHEN started_at IS NOT NULL AND ended_at IS NOT NULL
                        THEN greatest(0,extract(epoch FROM ended_at-started_at)*1000)::float8 END AS duration_ms
                FROM runs WHERE series_id=$1 AND run_day>=$2 AND run_day<$3 AND status BETWEEN 3 AND 6
            ), latest AS (
                SELECT status=3 AS succeeded FROM runs
                WHERE series_id=$1 AND run_day>=$2 AND run_day<$3 AND status BETWEEN 3 AND 6
                ORDER BY id DESC LIMIT 1
            ), cutoff AS (
                SELECT r.id FROM runs r CROSS JOIN latest l
                WHERE r.series_id=$1 AND r.run_day>=$2 AND r.run_day<$3 AND r.status BETWEEN 3 AND 6
                    AND (r.status=3)<>l.succeeded
                ORDER BY r.id DESC LIMIT 1
            ), streak AS (
                SELECT count(*) AS runs FROM runs r CROSS JOIN latest l
                WHERE r.series_id=$1 AND r.run_day>=$2 AND r.run_day<$3 AND r.status BETWEEN 3 AND 6
                    AND (r.id>(SELECT id FROM cutoff) OR NOT EXISTS(SELECT 1 FROM cutoff))
            )
            SELECT run_day,count(*),count(*) FILTER (WHERE status=3),count(duration_ms),
                percentile_cont(ARRAY[0.5,0.95]) WITHIN GROUP (ORDER BY duration_ms),
                (SELECT succeeded FROM latest),(SELECT runs FROM streak)
            FROM completed GROUP BY GROUPING SETS ((run_day),())
            """);
        query.Parameters.AddWithValue(series);
        query.Parameters.AddWithValue(window.From);
        query.Parameters.AddWithValue(window.To);
        var days = new Dictionary<DateOnly, TrendMetrics>();
        TrendMetrics? totals = null;
        SeriesTrendStreak? streak = null;
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var runs = reader.GetInt64(1);
                var successes = reader.GetInt64(2);
                var observed = reader.GetInt64(3);
                var quantiles = reader.IsDBNull(4) ? null : reader.GetFieldValue<double[]>(4);
                if (successes < 0 || successes > runs || observed < 0 || observed > runs ||
                    quantiles is { Length: not 2 } || observed > 0 && quantiles is null ||
                    observed == 0 && quantiles is not null ||
                    quantiles is not null && (quantiles.Any(value => !double.IsFinite(value) || value < 0) || quantiles[1] < quantiles[0]))
                    throw new InvalidDataException("Invalid committed trend aggregate.");
                var metrics = new TrendMetrics(runs, successes, runs - successes, observed,
                    runs == 0 ? null : (double)successes / runs, quantiles?[0], quantiles?[1]);
                if (reader.IsDBNull(0))
                {
                    totals = metrics;
                    streak = new(reader.IsDBNull(5) ? null : reader.GetBoolean(5) ? "Succeeded" : "Failed", reader.GetInt64(6));
                }
                else days.Add(reader.GetFieldValue<DateOnly>(0), metrics);
            }
        }
        if (totals is null || streak is null || streak.Runs < 0 || streak.Runs > totals.Runs ||
            (streak.Outcome is null) != (streak.Runs == 0))
            throw new InvalidDataException("Missing or invalid committed trend totals.");
        var empty = new TrendMetrics(0, 0, 0, 0, null, null, null);
        return new(series, window.Name, window.From, window.To, totals, streak,
            Enumerable.Range(0, window.Days).Select(offset =>
            {
                var day = window.From.AddDays(offset);
                return new SeriesTrendDay(day, days.GetValueOrDefault(day, empty));
            }).ToImmutableArray());
    }

    private static IResult Problem(int status, string title, string reason) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
