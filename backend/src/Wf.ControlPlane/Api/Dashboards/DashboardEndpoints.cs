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
public sealed record DashboardResponse(string Scope, string Id, string Window, DateOnly From, DateOnly To, int Top,
    string ObservationKind, DashboardRankings Rankings);

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
            var rows = await Query(source, filter, types, workflow, series, context.RequestAborted);
            if (scope == "node" && rows.Count == 0)
            {
                await using var exists = source.CreateCommand("""
                    SELECT EXISTS(SELECT 1 FROM node_runs n JOIN runs r ON r.run_day=n.run_day AND r.id=n.run_id
                        WHERE r.workflow_id=$1 AND n.node_key=$2)
                    OR EXISTS(SELECT 1 FROM workflow_versions w, jsonb_array_elements(w.manifest->'nodes') n
                        WHERE w.workflow_id=$1 AND n->>'key'=$2)
                    """);
                exists.Parameters.AddWithValue(workflow!);
                exists.Parameters.AddWithValue(filter.NodeKey!);
                if (await exists.ExecuteScalarAsync(context.RequestAborted) is not true) return Denied();
            }
            return Results.Json(new DashboardResponse(scope, id, filter.Window.Name, filter.Window.From, filter.Window.To,
                filter.Top, scope == "machine" ? "TaskAttempt" : "Node", DashboardQuery.Rank(rows, filter.Top)),
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

    private static async Task<List<DashboardRow>> Query(NpgsqlDataSource source, DashboardQuery filter, List<string> types,
        string? workflow, Guid? series, CancellationToken token)
    {
        await using var query = source.CreateCommand();
        query.Parameters.AddWithValue(filter.Window.From);
        query.Parameters.AddWithValue(filter.Window.To);
        query.Parameters.AddWithValue(types.ToArray());
        var predicates = new List<string>
        {
            "r.run_day>=$1 AND r.run_day<$2 AND r.status BETWEEN 3 AND 6",
            "r.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'",
            "split_part(r.workflow_id,'/',1)||'/'||split_part(r.workflow_id,'/',2)=ANY($3)"
        };
        if (workflow is not null) predicates.Add("r.workflow_id=" + P(workflow, NpgsqlDbType.Text));
        if (series is not null) predicates.Add("r.series_id=" + P(series.Value, NpgsqlDbType.Uuid));
        var machine = filter.Scope == "machine";
        var node = filter.NodeKey is null ? "" : " AND n.node_key=" + P(filter.NodeKey, NpgsqlDbType.Text);
        var agent = machine ? P(filter.ResourceId!.Value, NpgsqlDbType.Uuid) : "";
        query.CommandText = """
            WITH completed AS MATERIALIZED (SELECT r.run_day,r.id,r.workflow_id FROM runs r WHERE
            """ + " " + string.Join(" AND ", predicates) + "), " + (machine ? """
            observations AS (
                SELECT r.workflow_id,split_part(a.task_key,'/',1) AS node_key,r.id,a.status,
                    a.started_at,a.ended_at,a.resource_max_cpu_percent AS cpu,
                    a.resource_peak_ram_bytes/1048576::numeric AS ram
                FROM completed r JOIN task_attempts a ON a.run_day=r.run_day AND a.run_id=r.id
                WHERE a.run_day>=$1 AND a.run_day<$2 AND a.status BETWEEN 5 AND 11 AND a.agent_id=
            """ + agent + ")" : """
            resources AS (
                SELECT a.run_day,a.run_id,split_part(a.task_key,'/',1) AS node_key,
                    max(a.resource_max_cpu_percent) AS cpu,max(a.resource_peak_ram_bytes)/1048576::numeric AS ram
                FROM task_attempts a JOIN completed r ON r.run_day=a.run_day AND r.id=a.run_id
                WHERE a.run_day>=$1 AND a.run_day<$2 GROUP BY a.run_day,a.run_id,split_part(a.task_key,'/',1)
            ), observations AS (
                SELECT r.workflow_id,n.node_key,r.id,n.status,n.started_at,n.ended_at,a.cpu,a.ram
                FROM completed r JOIN node_runs n ON n.run_day=r.run_day AND n.run_id=r.id
                LEFT JOIN resources a ON a.run_day=n.run_day AND a.run_id=n.run_id AND a.node_key=n.node_key
                WHERE n.run_day>=$1 AND n.run_day<$2 AND n.status BETWEEN 5 AND 11
            """ + node + ")") + """
            , measured AS (
                SELECT *,CASE WHEN started_at IS NOT NULL AND ended_at IS NOT NULL
                    THEN greatest(0,extract(epoch FROM ended_at-started_at)*1000)::float8 END AS duration_ms
                FROM observations
            )
            SELECT workflow_id,node_key,count(*),count(*) FILTER (WHERE status IN (7,8,9)),
                count(*) FILTER (WHERE status=6),count(duration_ms),
                percentile_cont(ARRAY[0.5,0.95]) WITHIN GROUP (ORDER BY duration_ms),max(cpu),max(ram),
                max(id::text COLLATE "C")::uuid
            FROM measured GROUP BY workflow_id,node_key
            """;
        var rows = new List<DashboardRow>();
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var count = reader.GetInt64(2);
                var failed = reader.GetInt64(3);
                var soft = reader.GetInt64(4);
                var observed = reader.GetInt64(5);
                var durations = reader.IsDBNull(6) ? null : reader.GetFieldValue<double[]>(6);
                double? cpu = reader.IsDBNull(7) ? null : reader.GetDouble(7);
                decimal? ram = reader.IsDBNull(8) ? null : reader.GetDecimal(8);
                if (count <= 0 || failed < 0 || soft < 0 || failed + soft > count || observed < 0 || observed > count ||
                    (observed == 0) != (durations is null) || durations is { Length: not 2 } ||
                    durations is not null && (durations.Any(value => !double.IsFinite(value) || value < 0) || durations[0] > durations[1]) ||
                    cpu is { } value && (!double.IsFinite(value) || value < 0) || ram < 0)
                    throw new InvalidDataException("Invalid committed dashboard aggregate.");
                var run = reader.GetGuid(9);
                var key = reader.GetString(1);
                rows.Add(new(reader.GetString(0), key, count, failed, soft, observed, (double)failed / count,
                    durations?[0], durations?[1], cpu, ram, run, $"/runs/{run:D}"));
            }
        }
        return rows;

        string P(object value, NpgsqlDbType type) => RunQueryEndpoints.Parameter(query, value, type);
    }

    private static IResult Denied() => Results.Problem(TeamAuthorizationExtensions.Forbidden());
    private static IResult Problem(int status, string title, string reason) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
