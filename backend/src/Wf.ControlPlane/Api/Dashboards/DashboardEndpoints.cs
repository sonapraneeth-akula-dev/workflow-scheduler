using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api.Series;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Dashboards;

public sealed record DashboardRow(string WorkflowId, string NodeKey, long Observations, long Failures,
    long SoftFailures, long DurationCount, double FailureRate, double? P50Ms, double? P95Ms,
    double? CpuMaxPercent, decimal? RamPeakMb, Guid LatestRunId, string RunHref);
public sealed record DashboardRankings(ImmutableArray<DashboardRow> Slowest, ImmutableArray<DashboardRow> FailureRate,
    ImmutableArray<DashboardRow> Cpu, ImmutableArray<DashboardRow> Ram);
public sealed record WorkflowStatusCounts(long Queued, long Running, long Aborting, long Succeeded,
    long Failed, long Aborted, long TimedOut);
public sealed record WorkflowMetrics(long Runs, long Completed, long DurationCount, WorkflowStatusCounts Status,
    double? FailureRate, double? P50Ms, double? P90Ms, double? P99Ms, double? P999Ms,
    double? CpuMaxPercent, decimal? RamPeakMb);
public sealed record WorkflowDay(DateOnly Day, WorkflowMetrics Metrics);
public sealed record WorkflowTrends(WorkflowMetrics Total, ImmutableArray<WorkflowDay> Days);
public sealed record DashboardResponse(string Scope, string Id, string Window, DateOnly From, DateOnly To, int Top,
    string ObservationKind, DashboardRankings Rankings, WorkflowTrends? Trends = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardResponse))]
public partial class DashboardJson : JsonSerializerContext;

public sealed record DashboardQuery(string Scope, string Id, string? WorkflowId, string? NodeKey, Guid? ResourceId,
    TrendWindow Window, int Top)
{
    public static string Encode(string identity) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(identity));

    public static DashboardQuery Parse(string scope, string id, IQueryCollection query, DateTimeOffset now)
    {
        if (query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("window" or "top")))
            throw new ArgumentException("Use one window and top filter.");
        var window = TrendWindow.Parse(new QueryCollection(query.Where(pair => pair.Key == "window")
            .ToDictionary(pair => pair.Key, pair => pair.Value)), now);
        var top = 10;
        if (query.TryGetValue("top", out var values) &&
            (!int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out top) ||
                top is < 1 or > 50 || values[0] != top.ToString(CultureInfo.InvariantCulture)))
            throw new ArgumentException("top must be a canonical integer from 1 to 50.");
        if (scope is "schedule" or "machine")
        {
            if (!Guid.TryParseExact(id, "D", out var resource) || resource == Guid.Empty || id != resource.ToString("D"))
                throw new ArgumentException("Use a canonical nonempty UUID.");
            return new(scope, id, null, null, resource, window, top);
        }
        if (scope is not ("workflow" or "node") || id.Length is < 1 or > 4096)
            throw new ArgumentException("Invalid dashboard scope or identity.");
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(id)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        { throw new ArgumentException("Invalid dashboard identity."); }
        var parts = decoded.Split('/');
        var workflow = string.Join('/', parts.Take(3));
        if (Encode(decoded) != id || parts.Length != (scope == "node" ? 4 : 3) ||
            !WorkflowIdentity.TryGetTypeScope(workflow, out _) ||
            scope == "node" && !CatalogQuery.ValidSegment(parts[3]))
            throw new ArgumentException("Encode an exact team/type/workflow[/node] as unpadded base64url UTF8.");
        return new(scope, id, workflow, scope == "node" ? parts[3] : null, null, window, top);
    }

    public static DashboardRankings Rank(IEnumerable<DashboardRow> rows, int top)
    {
        var items = rows.ToArray();
        return new(
            Select(row => row.P95Ms),
            items.OrderByDescending(row => row.FailureRate).ThenBy(row => row.WorkflowId, StringComparer.Ordinal)
                .ThenBy(row => row.NodeKey, StringComparer.Ordinal).Take(top).ToImmutableArray(),
            Select(row => row.CpuMaxPercent),
            items.Where(row => row.RamPeakMb is not null).OrderByDescending(row => row.RamPeakMb)
                .ThenBy(row => row.WorkflowId, StringComparer.Ordinal).ThenBy(row => row.NodeKey, StringComparer.Ordinal)
                .Take(top).ToImmutableArray());

        ImmutableArray<DashboardRow> Select(Func<DashboardRow, double?> value) => items.Where(row => value(row) is not null)
            .OrderByDescending(value).ThenBy(row => row.WorkflowId, StringComparer.Ordinal)
            .ThenBy(row => row.NodeKey, StringComparer.Ordinal).Take(top).ToImmutableArray();
    }
}

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/dashboards/{scope}/{id}", Read).RequireAuthorization()
            .Produces<DashboardResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Fresh Viewer-scoped node rankings. scope=workflow|schedule|node|machine; workflow/node id " +
                "is canonical unpadded base64url UTF8 of team/type/workflow[/node], schedule/machine id a lowercase UUID. " +
                "window=7d|30d|90d (default 30d), UTC creation days including today; top=1..50 (default 10). " +
                "Terminal runs only. Workflow/schedule/node observations are terminal node occurrences; machine observations " +
                "are terminal task attempts executed on that exact agent, NEVER whole-node timings or other agents' resources. " +
                "Machine results include only currently granted workflow types, without global machine metadata. " +
                "Schedule history survives deletion. Slowest ranks exact source p95; failures count hard failure, timeout " +
                "and cancellation, soft failures separately. Rates are 0..1 fractions; skipped/reused occurrences count " +
                "without invented duration. Missing measurements stay null and are excluded from their ranking. " +
                "CPU and RAM are maxima across all attempts including failed retries; RAM in MiB. " +
                "Ties use ordinal workflow/node order. LatestRunId/RunHref identify a contributing run, not a p95 exemplar. " +
                "Workflow dashboards also return UTC daily trends and whole-window totals: all current run statuses, " +
                "terminal-run execution latency (started to ended, excluding queue time), exact p50/p90/p99/p99.9, " +
                "failure fraction of terminal runs, and maximum node CPU/RAM. Missing days have zero counts and null measurements. " +
                "No metadata, variables, errors, output or storage capabilities. Unknown/foreign identities return 403.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "window", "top" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query,
                        Schema = name == "top" ? new OpenApiSchema
                        { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "50", Default = JsonValue.Create(10) } :
                        new OpenApiSchema { Type = JsonSchemaType.String, Default = JsonValue.Create("30d"),
                            Enum = new[] { "7d", "30d", "90d" }.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() }
                    });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string scope, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, TimeProvider time, ILogger<DashboardResponse> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        DashboardQuery filter;
        try
        {
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/dashboards/{scope}/{id}")
                throw new ArgumentException("Noncanonical dashboard path.");
            filter = DashboardQuery.Parse(scope, id, context.Request.Query, time.GetUtcNow());
        }
        catch (ArgumentException) { return Problem(400, "Invalid dashboard query", "invalid_dashboard_query"); }
        try
        {
            Guid? series = null;
            var workflow = filter.WorkflowId;
            if (scope == "schedule")
            {
                await using var lookup = source.CreateCommand("""
                    SELECT s.workflow_id,r.id FROM schedules s JOIN run_series r
                        ON r.schedule_id=s.id AND r.workflow_id=s.workflow_id WHERE s.id=$1
                    """);
                lookup.Parameters.AddWithValue(filter.ResourceId!.Value);
                await using var reader = await lookup.ExecuteReaderAsync(context.RequestAborted);
                if (!await reader.ReadAsync(context.RequestAborted)) return Denied();
                workflow = reader.GetString(0);
                series = reader.GetGuid(1);
                if (await reader.ReadAsync(context.RequestAborted)) throw new InvalidDataException("Ambiguous schedule owner.");
            }
            var runFilter = new RunQuery(null, null, workflow, series, null, null, null, new Dictionary<string, string>(), 50, null, null);
            if (workflow is not null && !WorkflowIdentity.TryGetTypeScope(workflow, out _)) return Denied();
            var (types, error) = await RunQueryEndpoints.AuthorizeFilter(context, source, authorization, runFilter, includeRetainedSeries: true);
            if (error is not null) return error;
            if (scope == "machine")
            {
                await using var exists = source.CreateCommand(context.User.IsInRole(AuthExtensions.Admin) ?
                    "SELECT EXISTS(SELECT 1 FROM agents WHERE id=$1)" : """
                    SELECT EXISTS(SELECT 1 FROM task_attempts a JOIN runs r ON r.run_day=a.run_day AND r.id=a.run_id
                        WHERE a.agent_id=$1 AND r.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'
                            AND split_part(r.workflow_id,'/',1)||'/'||split_part(r.workflow_id,'/',2)=ANY($2))
                    """);
                exists.Parameters.AddWithValue(filter.ResourceId!.Value);
                if (!context.User.IsInRole(AuthExtensions.Admin)) exists.Parameters.AddWithValue(types.ToArray());
                if (await exists.ExecuteScalarAsync(context.RequestAborted) is not true) return Denied();
            }
            await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
            await using var transaction = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead, context.RequestAborted);
            var rows = await Query(connection, filter, types, workflow, series, context.RequestAborted);
            var trends = scope == "workflow"
                ? await QueryTrends(connection, filter, context.RequestAborted) : null;
            if (scope == "node" && rows.Count == 0)
            {
                await using var exists = connection.CreateCommand();
                exists.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM node_runs n JOIN runs r ON r.run_day=n.run_day AND r.id=n.run_id
                        WHERE r.workflow_id=$1 AND n.node_key=$2)
                    OR EXISTS(SELECT 1 FROM workflow_versions w, jsonb_array_elements(w.manifest->'nodes') n
                        WHERE w.workflow_id=$1 AND n->>'key'=$2)
                    """;
                exists.Parameters.AddWithValue(workflow!);
                exists.Parameters.AddWithValue(filter.NodeKey!);
                if (await exists.ExecuteScalarAsync(context.RequestAborted) is not true) return Denied();
            }
            await transaction.CommitAsync(context.RequestAborted);
            return Results.Json(new DashboardResponse(scope, id, filter.Window.Name, filter.Window.From, filter.Window.To,
                filter.Top, scope == "machine" ? "TaskAttempt" : "Node", DashboardQuery.Rank(rows, filter.Top), trends),
                DashboardJson.Default.DashboardResponse);
        }
        catch (NpgsqlException error)
        {
            logger.LogError(error, "Dashboard query unavailable.");
            return Problem(503, "Dashboards are unavailable", "dashboard_unavailable");
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException or InvalidCastException)
        {
            logger.LogError(error, "Committed dashboard data is inconsistent.");
            return Problem(500, "Committed dashboard data is inconsistent", "dashboard_inconsistent");
        }
    }

    private static NpgsqlCommand CreateQuery(NpgsqlConnection connection, DashboardQuery filter, List<string> types,
        string? workflow, Guid? series)
    {
        var query = connection.CreateCommand();
        query.Parameters.AddWithValue(filter.Window.From);
        query.Parameters.AddWithValue(filter.Window.To);
        query.Parameters.AddWithValue(types.ToArray());
        var predicates = new List<string> { "r.day>=$1 AND r.day<$2" };
        if (workflow is not null)
        {
            predicates.Add("r.workflow_id=" + P(workflow, NpgsqlDbType.Text));
        }
        else
        {
            predicates.Add("r.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'");
            predicates.Add("split_part(r.workflow_id,'/',1)||'/'||split_part(r.workflow_id,'/',2)=ANY($3)");
        }
        var scope = series is not null ? "schedule" : filter.Scope == "machine" ? "machine" : "workflow";
        predicates.Add("r.scope=" + P(scope, NpgsqlDbType.Text));
        predicates.Add("r.scope_id=" + P(series ?? (filter.Scope == "machine" ? filter.ResourceId!.Value : Guid.Empty), NpgsqlDbType.Uuid));
        if (filter.NodeKey is not null) predicates.Add("r.node_key=" + P(filter.NodeKey, NpgsqlDbType.Text));
        query.CommandText = """
            SELECT r.workflow_id,r.node_key,r.observations,r.failures,r.soft_failures,r.duration_count,
                r.durations_ms,r.cpu_max_pct,r.ram_peak_mb,r.latest_run_id
            FROM dashboard_rollups r WHERE
            """ + " " + string.Join(" AND ", predicates);
        return query;

        string P(object value, NpgsqlDbType type) => RunQueryEndpoints.Parameter(query, value, type);
    }

    private static async Task<List<DashboardRow>> Query(NpgsqlConnection connection, DashboardQuery filter, List<string> types,
        string? workflow, Guid? series, CancellationToken token)
    {
        await using var query = CreateQuery(connection, filter, types, workflow, series);
        var rows = new List<(DashboardRow Row, double[] Durations)>();
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var count = reader.GetInt64(2);
                var failed = reader.GetInt64(3);
                var soft = reader.GetInt64(4);
                var observed = reader.GetInt64(5);
                var durations = reader.GetFieldValue<double[]>(6);
                double? cpu = reader.IsDBNull(7) ? null : reader.GetDouble(7);
                decimal? ram = reader.IsDBNull(8) ? null : reader.GetDecimal(8);
                if (count <= 0 || failed < 0 || soft < 0 || failed + soft > count || observed < 0 || observed > count ||
                    observed != durations.LongLength || durations.Any(value => !double.IsFinite(value) || value < 0) ||
                    cpu is { } value && (!double.IsFinite(value) || value < 0) || ram < 0)
                    throw new InvalidDataException("Invalid committed dashboard aggregate.");
                var run = reader.GetGuid(9);
                var key = reader.GetString(1);
                rows.Add((new(reader.GetString(0), key, count, failed, soft, observed, (double)failed / count,
                    null, null, cpu, ram, run, $"/runs/{run:D}"), durations));
            }
        }
        return rows.GroupBy(item => (item.Row.WorkflowId, item.Row.NodeKey)).Select(group =>
        {
            var items = group.Select(item => item.Row).ToArray();
            var durations = group.SelectMany(item => item.Durations).ToArray();
            Array.Sort(durations);
            var count = items.Sum(item => item.Observations);
            var failures = items.Sum(item => item.Failures);
            var latest = items.MaxBy(item => item.LatestRunId.ToString("D"), StringComparer.Ordinal)!;
            return latest with
            {
                Observations = count, Failures = failures, SoftFailures = items.Sum(item => item.SoftFailures),
                DurationCount = durations.LongLength, FailureRate = (double)failures / count,
                P50Ms = Percentile(durations, 0.5), P95Ms = Percentile(durations, 0.95),
                CpuMaxPercent = items.Max(item => item.CpuMaxPercent), RamPeakMb = items.Max(item => item.RamPeakMb)
            };
        }).ToList();
    }

    private static double? Percentile(double[] sorted, double fraction)
    {
        // Match PostgreSQL percentile_cont over sorted source observations.
        if (sorted.Length == 0) return null;
        var position = (sorted.Length - 1) * fraction;
        var lower = (int)position;
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    private static async Task<WorkflowTrends> QueryTrends(NpgsqlConnection connection, DashboardQuery filter, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Parameters.AddWithValue(filter.Window.From);
        query.Parameters.AddWithValue(filter.Window.To);
        query.Parameters.AddWithValue(filter.WorkflowId!);
        query.CommandText = """
            WITH daily AS MATERIALIZED (
                SELECT run_day AS day,count(*) AS runs,
                    count(*) FILTER(WHERE status BETWEEN 3 AND 6) AS completed,
                    count(*) FILTER(WHERE status=0) AS queued,count(*) FILTER(WHERE status=1) AS running,
                    count(*) FILTER(WHERE status=2) AS aborting,count(*) FILTER(WHERE status=3) AS succeeded,
                    count(*) FILTER(WHERE status=4) AS failed,count(*) FILTER(WHERE status=5) AS aborted,
                    count(*) FILTER(WHERE status=6) AS timed_out,
                    coalesce(array_agg(greatest(0,date_part('epoch',ended_at-started_at)*1000))
                        FILTER(WHERE status BETWEEN 3 AND 6 AND started_at IS NOT NULL AND ended_at IS NOT NULL),
                        '{}'::double precision[]) AS durations
                FROM runs WHERE run_day >= $1 AND run_day < $2 AND workflow_id=$3 GROUP BY run_day
            ), metrics AS (
                SELECT day,runs,completed,cardinality(durations)::bigint AS measured,
                    queued,running,aborting,succeeded,failed,aborted,timed_out,
                    (SELECT percentile_cont(ARRAY[0.5,0.9,0.99,0.999]) WITHIN GROUP(ORDER BY duration_ms)
                        FROM unnest(durations) d(duration_ms)) AS quantiles
                FROM daily
                UNION ALL
                SELECT NULL,coalesce(sum(runs),0)::bigint,coalesce(sum(completed),0)::bigint,
                    coalesce(sum(cardinality(durations)),0)::bigint,coalesce(sum(queued),0)::bigint,
                    coalesce(sum(running),0)::bigint,coalesce(sum(aborting),0)::bigint,coalesce(sum(succeeded),0)::bigint,
                    coalesce(sum(failed),0)::bigint,coalesce(sum(aborted),0)::bigint,coalesce(sum(timed_out),0)::bigint,
                    (SELECT percentile_cont(ARRAY[0.5,0.9,0.99,0.999]) WITHIN GROUP(ORDER BY duration_ms)
                        FROM daily CROSS JOIN LATERAL unnest(durations) d(duration_ms))
                FROM daily
            ), resources AS (
                SELECT day,max(cpu_max_pct) AS cpu,max(ram_peak_mb) AS ram
                FROM dashboard_rollups WHERE day >= $1 AND day < $2 AND workflow_id=$3
                    AND scope='workflow' AND scope_id='00000000-0000-0000-0000-000000000000'
                GROUP BY GROUPING SETS ((day),())
            )
            SELECT coalesce(m.day,r.day),coalesce(m.runs,0),coalesce(m.completed,0),coalesce(m.measured,0),
                coalesce(m.queued,0),coalesce(m.running,0),coalesce(m.aborting,0),coalesce(m.succeeded,0),
                coalesce(m.failed,0),coalesce(m.aborted,0),coalesce(m.timed_out,0),m.quantiles,r.cpu,r.ram
            FROM metrics m FULL JOIN resources r
                ON coalesce(m.day,'-infinity'::date)=coalesce(r.day,'-infinity'::date)
            """;
        var days = new Dictionary<DateOnly, WorkflowMetrics>();
        WorkflowMetrics? total = null;
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var runs = reader.GetInt64(1);
                var completed = reader.GetInt64(2);
                var measured = reader.GetInt64(3);
                var counts = Enumerable.Range(4, 7).Select(reader.GetInt64).ToArray();
                var quantiles = reader.IsDBNull(11) ? null : reader.GetFieldValue<double[]>(11);
                double? cpu = reader.IsDBNull(12) ? null : reader.GetDouble(12);
                decimal? ram = reader.IsDBNull(13) ? null : reader.GetDecimal(13);
                if (runs < 0 || counts.Any(count => count < 0) || counts.Sum() != runs ||
                    completed != counts.Skip(3).Sum() || measured < 0 || measured > completed ||
                    (measured == 0) != (quantiles is null) || quantiles is { Length: not 4 } ||
                    quantiles is not null && (quantiles.Any(value => !double.IsFinite(value) || value < 0) ||
                        !quantiles.SequenceEqual(quantiles.Order())) ||
                    cpu is { } value && (!double.IsFinite(value) || value < 0) || ram < 0)
                    throw new InvalidDataException("Invalid committed workflow trend aggregate.");
                var metrics = new WorkflowMetrics(runs, completed, measured,
                    new(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6]),
                    completed == 0 ? null : (double)counts.Skip(4).Sum() / completed,
                    quantiles?[0], quantiles?[1], quantiles?[2], quantiles?[3], cpu, ram);
                if (reader.IsDBNull(0)) total = metrics;
                else days.Add(reader.GetFieldValue<DateOnly>(0), metrics);
            }
        }
        var empty = new WorkflowMetrics(0, 0, 0, new(0, 0, 0, 0, 0, 0, 0), null, null, null, null, null, null, null);
        return new(total ?? throw new InvalidDataException("Missing workflow trend total."),
            Enumerable.Range(0, filter.Window.To.DayNumber - filter.Window.From.DayNumber)
                .Select(offset => filter.Window.From.AddDays(offset))
                .Select(day => new WorkflowDay(day, days.GetValueOrDefault(day, empty))).ToImmutableArray());
    }

    private static IResult Denied() => Results.Problem(TeamAuthorizationExtensions.Forbidden());
    private static IResult Problem(int status, string title, string reason) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
