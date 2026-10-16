using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class RunQueryEndpoints
{
    public static IEndpointRouteBuilder MapRunQueryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs", Read).RequireAuthorization()
            .Produces<RunQueryPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Safe durable run summaries, newest UUIDv7 first. All filters combine with AND. " +
                "Metadata matches the run's copied metadata, not its current workflow. Deleted workflows retain history. " +
                "Fresh Viewer grants filter every page; cursors are bound to filters and visible type scopes. " +
                "Includes only copied public metadata; no variable values, snapshots or storage capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "team", "type", "workflowId", "seriesId", "status", "from", "to", "limit", "cursor", "meta.*" })
                {
                    var schema = new OpenApiSchema { Type = name == "limit" ? JsonSchemaType.Integer : JsonSchemaType.String };
                    if (name == "limit") { schema.Minimum = "1"; schema.Maximum = "100"; schema.Default = JsonValue.Create(50); }
                    if (name is "from" or "to") schema.Format = "date-time";
                    if (name == "seriesId") schema.Format = "uuid";
                    if (name == "status") schema.Enum = Enum.GetNames<RunStatus>().Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Schema = schema,
                        Description = name switch
                        {
                            "team" => "Exact registered team; omitted lists only currently visible scopes.",
                            "type" => "Exact registered type name; requires team.",
                            "workflowId" => "Exact team/type/workflow identity; unknown or ungranted resource is forbidden.",
                            "seriesId" => "Canonical nonempty lowercase UUID; unknown, ambiguous or ungranted series is forbidden.",
                            "from" => "Inclusive created_at lower bound; explicit RFC3339 offset, microsecond precision.",
                            "to" => "Exclusive created_at upper bound; explicit RFC3339 offset, microsecond precision.",
                            "limit" => "Canonical integer 1–100, default 50. Keep all filters and limit unchanged on subsequent pages.",
                            "cursor" => "Opaque canonical nextCursor from this filter/scope combination; no offset paging.",
                            "meta.*" => "Dynamic query-name prefix, not a literal key: meta.environment=prod. Up to 32 exact string equality filters, ANDed; each suffix 1–128 characters, value ≤1024. Copied run metadata.values only.",
                            _ => "One exact case-sensitive RunStatus name."
                        }
                    });
                }
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, NpgsqlDataSource source, IAuthorizationService authorization)
    {
        context.Response.Headers.CacheControl = "no-store";
        RunQuery filter;
        try { filter = RunQuery.Parse(context.Request.Query); }
        catch (ArgumentException error) { return Problem(400, "Invalid run query", "invalid_run_query", error.Message); }
        try
        {
            var (types, error) = await AuthorizeFilter(context, source, authorization, filter);
            if (error is not null) return error;
            if (filter.CursorBinding is not null && filter.CursorBinding != filter.Binding(types))
                return Problem(400, "Invalid run cursor", "invalid_run_cursor", "Keep the original filters, page limit and currently authorized scopes.");
            return await Page(context, source, filter, types);
        }
        catch (NpgsqlException) { return Problem(503, "Run search is unavailable", "run_query_unavailable"); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        { return Problem(500, "Committed run data is inconsistent", "run_query_data_inconsistent"); }
    }

    internal static async Task<(List<string> Types, IResult? Error)> AuthorizeFilter(HttpContext context,
        NpgsqlDataSource source, IAuthorizationService authorization, RunQuery filter, bool includeRetainedSeries = false)
    {
        if (filter.WorkflowId is { } workflow)
        {
            WorkflowIdentity.TryGetTypeScope(workflow, out var type);
            if (!await Allowed(TeamResourceKind.Type, type)) return ([], Results.Problem(TeamAuthorizationExtensions.Forbidden()));
            await using var exists = source.CreateCommand("""
                SELECT EXISTS(SELECT 1 FROM workflows WHERE id=$1)
                    OR EXISTS(SELECT 1 FROM runs WHERE workflow_id=$1)
                    OR ($2 AND EXISTS(SELECT 1 FROM run_series WHERE workflow_id=$1))
                """);
            exists.Parameters.AddWithValue(workflow);
            exists.Parameters.AddWithValue(includeRetainedSeries);
            if (await exists.ExecuteScalarAsync(context.RequestAborted) is not true)
                return ([], Results.Problem(TeamAuthorizationExtensions.Forbidden()));
        }
        if (filter.SeriesId is { } series && !await Allowed(TeamResourceKind.Series, series.ToString("D")))
            return ([], Results.Problem(TeamAuthorizationExtensions.Forbidden()));
        var types = new List<string>();
        await using (var query = source.CreateCommand("""
            SELECT id FROM workflow_types WHERE ($1::text IS NULL OR team_id=$1)
                AND ($2::text IS NULL OR id=team_id||'/'||$2)
            ORDER BY id COLLATE "C"
            """))
        {
            query.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Team ?? DBNull.Value);
            query.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Type ?? DBNull.Value);
            var candidates = new List<string>();
            await using (var reader = await query.ExecuteReaderAsync(context.RequestAborted))
                while (await reader.ReadAsync(context.RequestAborted)) candidates.Add(reader.GetString(0));
            foreach (var type in candidates)
                if (await Allowed(TeamResourceKind.Type, type)) types.Add(type);
        }
        return (types, null);

        async Task<bool> Allowed(TeamResourceKind kind, string id) =>
            (await authorization.AuthorizeAsync(context.User, new TeamResource(kind, id, context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded;
    }

    private static async Task<IResult> Page(HttpContext context, NpgsqlDataSource source, RunQuery filter, List<string> types)
    {
        await using var query = source.CreateCommand();
        var where = Predicates(query, filter, types);
        query.CommandText = """
            SELECT r.id,r.workflow_id,r.version_id,r.series_id,r.status,r.created_at,r.started_at,r.ended_at,r.metadata::text
            FROM runs r WHERE
            """ + " " + string.Join(" AND ", where) + " ORDER BY r.id DESC LIMIT " + Parameter(query, filter.Limit + 1, NpgsqlDbType.Integer);
        var items = new List<RunQueryRow>();
        await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
        while (await reader.ReadAsync(context.RequestAborted))
        {
            var workflow = reader.GetString(1).Split('/');
            var status = (RunStatus)reader.GetInt16(4);
            if (!Enum.IsDefined(status)) throw new InvalidDataException("Unknown persisted run status.");
            items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), workflow[0], workflow[1], status.ToString(),
                reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7))
                { Metadata = RunMetadata.FromStored(reader.GetString(8)) });
        }
        var more = items.Count > filter.Limit;
        if (more) items.RemoveAt(items.Count - 1);
        return Results.Ok(new RunQueryPage(items, more ? filter.Cursor(items[^1].Id, types) : null));
    }

    internal static List<string> Predicates(NpgsqlCommand query, RunQuery filter, List<string> types)
    {
        var where = new List<string>
        {
            "r.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'",
            "split_part(r.workflow_id,'/',1)||'/'||split_part(r.workflow_id,'/',2)=ANY(" +
                Parameter(query, types.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text) + ")"
        };
        if (filter.WorkflowId is not null) where.Add("r.workflow_id=" + Parameter(query, filter.WorkflowId, NpgsqlDbType.Text));
        if (filter.SeriesId is not null) where.Add("r.series_id=" + Parameter(query, filter.SeriesId.Value, NpgsqlDbType.Uuid));
        if (filter.Status is not null) where.Add("r.status=" + Parameter(query, (short)filter.Status.Value, NpgsqlDbType.Smallint));
        if (filter.From is not null) where.Add("r.created_at>=" + Parameter(query, filter.From.Value, NpgsqlDbType.TimestampTz));
        if (filter.To is not null) where.Add("r.created_at<" + Parameter(query, filter.To.Value, NpgsqlDbType.TimestampTz));
        if (filter.AfterId is not null) where.Add("r.id<" + Parameter(query, filter.AfterId.Value, NpgsqlDbType.Uuid));
        if (filter.Metadata.Count > 0)
        {
            var json = new JsonObject { ["Values"] = new JsonObject(filter.Metadata.Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))) };
            where.Add("r.metadata @> " + Parameter(query, json.ToJsonString(), NpgsqlDbType.Jsonb));
        }
        return where;
    }

    internal static string Parameter(NpgsqlCommand query, object value, NpgsqlDbType type)
    {
        query.Parameters.AddWithValue(type, value);
        return "$" + query.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IResult Problem(int status, string title, string reason, string? detail = null) =>
        Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
