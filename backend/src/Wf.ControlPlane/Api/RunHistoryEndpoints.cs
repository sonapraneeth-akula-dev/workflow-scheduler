using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Api;

public static class RunHistoryEndpoints
{
    public static IEndpointRouteBuilder MapRunHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Map("/api/v1/runs/history", (HttpContext context, NpgsqlDataSource source, IAuthorizationService authorization) =>
            Read(context, source, authorization));
        Map("/api/v1/workflows/{team}/{type}/{workflow}/history",
            (HttpContext context, string team, string type, string workflow, NpgsqlDataSource source, IAuthorizationService authorization) =>
                Read(context, source, authorization, team + "/" + type + "/" + workflow));
        Map("/api/v1/series/{id}/history",
            (HttpContext context, string id, NpgsqlDataSource source, IAuthorizationService authorization) =>
                Read(context, source, authorization, series: id));
        return endpoints;

        void Map(string path, Delegate handler) => endpoints.MapGet(path, handler).RequireAuthorization()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.ResponseCacheAttribute { NoStore = true })
            .Produces<RunHistoryPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Durable workflow/series history: runs plus retained skipped/missed fire facts, including deleted resources. " +
                "Run filters share /runs semantics; from/to bound the row timestamp. status, abortedBy or meta.* exclude fire-only rows. " +
                "sort=createdAtDesc (default) or createdAtAsc; stable timestamp/UUID/kind keyset, bound to all filters, limit and fresh Viewer scopes. " +
                "outcome=Run|Skipped|Missed; abortedBy is an exact principal UUID from the first committed abort event, not inferred from status. " +
                "Run versionId is the executed workflow version, NOT code-schedule config. fire.scheduleConfigVersionId is the immutable config at claim, null for user/legacy fires. " +
                "currentScheduleId is the authoritative non-deleted schedule linked by run_series for an explicit seriesId; null after schedule deletion, for run-only legacy series or without a series filter. " +
                "Variables project only set schema-backed names/types; all values/defaults/secret references omitted, Secret entries marked redacted. Unknown legacy schemas produce []. " +
                "triggeredByName is the authenticated display name recorded at manual acceptance, null for legacy/unnamed principals and nonmanual triggers; never inferred from the reader. " +
                "Copied metadata is public author-declared metadata. Duration is endedAt-startedAt in milliseconds, null unless both exist. No storage capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "workflowId", "seriesId", "team", "type", "status", "from", "to", "meta.*", "limit", "cursor", "sort", "abortedBy", "outcome" })
                {
                    var schema = new OpenApiSchema { Type = name == "limit" ? JsonSchemaType.Integer : JsonSchemaType.String };
                    if (name == "limit") { schema.Minimum = "1"; schema.Maximum = "100"; schema.Default = JsonValue.Create(50); }
                    if (name is "from" or "to") schema.Format = "date-time";
                    if (name is "seriesId" or "abortedBy") schema.Format = "uuid";
                    var choices = name switch
                    {
                        "status" => Enum.GetNames<RunStatus>(), "sort" => ["createdAtDesc", "createdAtAsc"],
                        "outcome" => ["Run", "Skipped", "Missed"], _ => []
                    };
                    if (choices.Length > 0) schema.Enum = choices.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
                    operation.Parameters.Add(new OpenApiParameter { Name = name, In = ParameterLocation.Query, Schema = schema });
                }
                return Task.CompletedTask;
            });
    }

    private static async Task<IResult> Read(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization, string? workflow = null, string? series = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        RunHistoryQuery filter;
        try { filter = RunHistoryQuery.Parse(context.Request.Query, workflow, series); }
        catch (ArgumentException error) { return Problem(400, "Invalid history query", "invalid_run_history_query", error.Message); }
        try
        {
            var (types, error) = await RunQueryEndpoints.AuthorizeFilter(context, source, authorization, filter.Runs, includeRetainedSeries: true);
            if (error is not null) return error;
            if (filter.CursorBinding is not null && filter.CursorBinding != filter.Binding(types))
                return Problem(400, "Invalid history cursor", "invalid_run_history_cursor",
                    "Keep the original filters, page limit and currently authorized scopes.");
            Guid? scheduleId = null;
            if (filter.Runs.SeriesId is { } seriesId)
            {
                await using var schedule = source.CreateCommand("""
                    SELECT s.id FROM run_series r JOIN schedules s ON s.id=r.schedule_id AND s.workflow_id=r.workflow_id
                    WHERE r.id=$1 AND s.deleted_at IS NULL
                    """);
                schedule.Parameters.AddWithValue(seriesId);
                scheduleId = await schedule.ExecuteScalarAsync(context.RequestAborted) as Guid?;
            }
            return await Page(context, source, filter, types, scheduleId);
        }
        catch (NpgsqlException) { return Problem(503, "Run history is unavailable", "run_history_unavailable"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or
            InvalidOperationException or OverflowException or InvalidCastException or KeyNotFoundException)
        { return Problem(500, "Committed history data is inconsistent", "run_history_data_inconsistent"); }
    }

    private static async Task<IResult> Page(HttpContext context, NpgsqlDataSource source, RunHistoryQuery filter,
        List<string> types, Guid? scheduleId)
    {
        await using var query = source.CreateCommand();
        var where = RunQueryEndpoints.Predicates(query, filter.Runs, types);
        if (filter.Outcome is "Skipped" or "Missed") where.Add("false");
        if (filter.AbortedBy is { } actor)
            where.Add("""
                (SELECT convert_from(e.payload,'UTF8')::jsonb->'Abort'->>'By' FROM run_events e
                    WHERE e.run_day=r.run_day AND e.run_id=r.id AND e.kind=0
                        AND convert_from(e.payload,'UTF8')::jsonb->'Abort' IS NOT NULL
                        AND convert_from(e.payload,'UTF8')::jsonb->'Abort'<>'null'::jsonb
                    ORDER BY e.seq LIMIT 1)=
                """ + P(actor, NpgsqlDbType.Text));
        var fires = new List<string>
        {
            "f.outcome IN (1,2)", "s.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'",
            "split_part(s.workflow_id,'/',1)||'/'||split_part(s.workflow_id,'/',2)=ANY(" +
                P(types.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text) + ")"
        };
        if (filter.Runs.WorkflowId is { } workflow) fires.Add("s.workflow_id=" + P(workflow, NpgsqlDbType.Text));
        if (filter.Runs.SeriesId is { } series) fires.Add("s.id=" + P(series, NpgsqlDbType.Uuid));
        if (filter.Runs.From is { } from) fires.Add("f.fire_at>=" + P(from, NpgsqlDbType.TimestampTz));
        if (filter.Runs.To is { } to) fires.Add("f.fire_at<" + P(to, NpgsqlDbType.TimestampTz));
        if (filter.Runs.Status is not null || filter.AbortedBy is not null || filter.Runs.Metadata.Count > 0 || filter.Outcome == "Run")
            fires.Add("false");
        if (filter.Outcome is "Skipped" or "Missed") fires.Add("f.outcome=" + (filter.Outcome == "Skipped" ? "1" : "2"));
        var after = filter.After is not { } position ? "" : " WHERE (at,key_id,kind)" +
            (filter.Ascending ? ">" : "<") + "(" + P(position.At, NpgsqlDbType.TimestampTz) + "," +
            P(position.Id, NpgsqlDbType.Uuid) + "," + P(position.Kind, NpgsqlDbType.Integer) + ")";
        var direction = filter.Ascending ? " ASC" : " DESC";
        query.CommandText = """
            WITH facts AS (
                SELECT r.created_at AS at,r.id AS key_id,0 AS kind,r.workflow_id,r.series_id,r.id AS run_id,
                    r.version_id,r.status,r.started_at,r.ended_at,r.trigger,r.metadata,
                    NULL::uuid AS schedule_id,NULL::timestamptz AS fire_at,NULL::smallint AS outcome,
                    NULL::text AS reason,NULL::uuid AS config_version_id,r.run_day
                FROM runs r WHERE
            """ + " " + string.Join(" AND ", where) + """
                UNION ALL
                SELECT f.fire_at,f.schedule_id,1,s.workflow_id,s.id,f.run_id,
                    NULL,NULL,NULL,NULL,NULL,NULL,f.schedule_id,f.fire_at,f.outcome,f.reason,f.config_version_id,NULL
                FROM run_series s JOIN schedule_fires f ON f.schedule_id=s.schedule_id WHERE
            """ + " " + string.Join(" AND ", fires) + """
            ), page AS (SELECT * FROM facts
            """ + after + " ORDER BY at" + direction + ",key_id" + direction + ",kind" + direction +
            " LIMIT " + P(filter.Runs.Limit + 1, NpgsqlDbType.Integer) + """
            )
            SELECT p.*,a.record::text,
                COALESCE((SELECT jsonb_agg(jsonb_build_object('key',v->>'key','type',v->>'type'))
                    FROM workflow_versions w CROSS JOIN LATERAL jsonb_array_elements(COALESCE(w.manifest->'variables','[]')) v
                    WHERE w.id=p.version_id AND w.workflow_id=p.workflow_id
                        AND r.variables ? (v->>'key')),'[]'::jsonb)::text AS variable_schema,
                sf.schedule_id,sf.fire_at,sf.outcome,sf.reason,sf.config_version_id,executed.semver,r.triggered_by_name
            FROM page p
            LEFT JOIN workflow_versions executed ON p.kind=0 AND executed.id=p.version_id AND executed.workflow_id=p.workflow_id
            LEFT JOIN runs r ON p.kind=0 AND r.run_day=p.run_day AND r.id=p.run_id
            LEFT JOIN LATERAL (
                SELECT convert_from(e.payload,'UTF8')::jsonb->'Abort' AS record FROM run_events e
                WHERE p.kind=0 AND e.run_day=p.run_day AND e.run_id=p.run_id AND e.kind=0
                    AND convert_from(e.payload,'UTF8')::jsonb->'Abort' IS NOT NULL
                    AND convert_from(e.payload,'UTF8')::jsonb->'Abort'<>'null'::jsonb
                ORDER BY e.seq LIMIT 1
            ) a ON true
            LEFT JOIN LATERAL (
                SELECT f.* FROM schedule_fires f JOIN run_series s ON s.schedule_id=f.schedule_id
                WHERE p.kind=0 AND f.run_id=p.run_id AND s.workflow_id=p.workflow_id AND s.id=p.series_id
                ORDER BY f.fire_at,f.schedule_id LIMIT 1
            ) sf ON true
            ORDER BY p.at
            """ + direction + ",p.key_id" + direction + ",p.kind" + direction;
        var rows = new List<(RunHistoryRow Row, RunHistoryPosition Position)>();
        await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
        while (await reader.ReadAsync(context.RequestAborted))
        {
            var kind = reader.GetInt32(2);
            var at = reader.GetFieldValue<DateTimeOffset>(0);
            var started = Time(8);
            var ended = Time(9);
            LiveAbort? abort = null;
            if (!reader.IsDBNull(18))
            {
                var record = JsonSerializer.Deserialize(reader.GetString(18), RunStoreJson.Default.AbortRecord)
                    ?? throw new InvalidDataException();
                abort = new(record.By, record.ByName, record.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    record.Reason, record.Source.ToString());
            }
            RunHistoryFire? fire = null;
            var offset = kind == 1 ? 12 : 20;
            if (!reader.IsDBNull(offset))
                fire = new(reader.GetGuid(offset), reader.GetFieldValue<DateTimeOffset>(offset + 1),
                    reader.GetInt16(offset + 2) switch { 0 => "Started", 1 => "Skipped", 2 => "Missed", 3 => "CaughtUp", _ => throw new InvalidDataException() },
                    reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3), Uuid(offset + 4));
            var status = reader.IsDBNull(7) ? (RunStatus?)null : (RunStatus)reader.GetInt16(7);
            var trigger = reader.IsDBNull(10) ? (RunTrigger?)null : (RunTrigger)reader.GetInt16(10);
            if (status is { } st && !Enum.IsDefined(st) || trigger is { } tr && !Enum.IsDefined(tr) ||
                started is not null && ended < started) throw new InvalidDataException();
            rows.Add((new(kind == 0 ? "Run" : "Fire", at, reader.GetString(3), Uuid(4), Uuid(5), Uuid(6),
                status?.ToString(), started, ended, started is not null && ended is not null ? (ended.Value - started.Value).TotalMilliseconds : null,
                trigger?.ToString(), reader.IsDBNull(11) ? null : RunMetadata.FromStored(reader.GetString(11)),
                abort, RunHistoryQuery.Variables(reader.GetString(19)), fire)
            {
                Version = reader.IsDBNull(25) ? null : reader.GetString(25),
                TriggeredByName = trigger == RunTrigger.Manual && !reader.IsDBNull(26) ? reader.GetString(26) : null
            }, new(at, reader.GetGuid(1), kind)));
        }
        var more = rows.Count > filter.Runs.Limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        await reader.DisposeAsync();
        var failures = await RunFailures.Summaries(context, source,
            rows.Where(row => row.Row.Kind == "Run" && row.Row.RunId.HasValue).Select(row => row.Row.RunId!.Value));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            rows[index] = (row.Row with
            {
                FailureSummary = row.Row.Kind == "Run" && row.Row.RunId is { } id ? failures.GetValueOrDefault(id) : null
            }, row.Position);
        }
        return Results.Ok(new RunHistoryPage(rows.Select(row => row.Row).ToArray(),
            more ? filter.Cursor(rows[^1].Position, types) : null) { CurrentScheduleId = scheduleId });

        string P(object value, NpgsqlDbType type) => RunQueryEndpoints.Parameter(query, value, type);
        Guid? Uuid(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
        DateTimeOffset? Time(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private static IResult Problem(int status, string title, string reason, string? detail = null) =>
        Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
