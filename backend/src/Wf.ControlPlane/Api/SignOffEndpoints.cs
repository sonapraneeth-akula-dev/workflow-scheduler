using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class SignOffEndpoints
{
    private const long MaximumRequestBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapSignOffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        foreach (var path in new[] { "/api/v1/runs/{id}/signoff", "/api/v1/runs/{id}/reports/{reportId}/signoff" })
        {
            Describe(endpoints.MapGet(path, Read), false);
            Describe(endpoints.MapPost(path, Sign), true);
            Describe(endpoints.MapPost(path + "/revoke", Revoke), true);
        }
        return endpoints;
    }

    private static void Describe(RouteHandlerBuilder endpoint, bool write)
    {
        endpoint.RequireAuthorization().Produces<SignOffResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(412).ProducesProblem(413).ProducesProblem(415)
            .ProducesProblem(428).ProducesProblem(503)
            .WithDescription("Run-scoped approval; reports are independent. Fresh Viewer reads; Operator/Admin writes. " +
                "Only terminal runs may change approval. Required reason (1-2000 characters) and authenticated actor " +
                "are recorded in append-only history. No inheritance or execution gating. " +
                "Writes require the current strong ETag in If-Match; GET includes current state and newest-first history.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in write ? new[] { "If-Match" } : ["before", "limit"])
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = write ? ParameterLocation.Header : ParameterLocation.Query, Required = write,
                        Description = write ? "Exact strong ETag from GET, including quotes (initially \"0\")." :
                            name == "before" ? "Exclusive positive decimal revision cursor." : "Page size 1-50; default 20.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                return Task.CompletedTask;
            });
        if (write) endpoint.Accepts<SignOffRequest>("application/json");
    }

    public static bool TryRevision(string? value, out long revision) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision >= 0 &&
        value == revision.ToString(CultureInfo.InvariantCulture);

    private static Task<IResult> Read(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization, TimeProvider time) => Handle(context, source, authorization, time, null);
    private static Task<IResult> Sign(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization, TimeProvider time) => Handle(context, source, authorization, time, SignOffAction.SignOff);
    private static Task<IResult> Revoke(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization, TimeProvider time) => Handle(context, source, authorization, time, SignOffAction.Revoke);

    private static async Task<IResult> Handle(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization, TimeProvider time, SignOffAction? action)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        Guid runId;
        Guid? reportId = null;
        try
        {
            var id = context.Request.RouteValues["id"]?.ToString() ?? "";
            runId = RunEventsQuery.RunId(id);
            if (id != runId.ToString("D")) throw new ArgumentException();
            if (context.Request.RouteValues["reportId"] is { } report)
            {
                reportId = RunEventsQuery.RunId(report.ToString()!);
                if (report.ToString() != reportId.Value.ToString("D")) throw new ArgumentException();
            }
            var path = $"/api/v1/runs/{id}" + (reportId is { } r ? $"/reports/{r:D}" : "") +
                "/signoff" + (action == SignOffAction.Revoke ? "/revoke" : "");
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != path)
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Problem(400, "Invalid sign-off identity"); }
        var token = context.RequestAborted;
        try
        {
            if (!(await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, runId.ToString("D"), token),
                TeamAuthorizationExtensions.Policy(action is null ? RegistryRole.Viewer : RegistryRole.Operator))).Succeeded)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            long? before = null;
            var limit = 20;
            if (context.Request.Query.Keys.Any(key => action is not null || key is not ("before" or "limit")))
                return Problem(400, "Invalid sign-off query");
            foreach (var (key, values) in context.Request.Query)
            {
                if (values.Count != 1 || !TryRevision(values[0], out var number) || number == 0)
                    return Problem(400, "Invalid sign-off query");
                if (key == "before") before = number;
                else if (number > 50) return Problem(400, "Sign-off history limit must be between 1 and 50");
                else limit = (int)number;
            }
            long expected = 0;
            SignOffRequest? request = null;
            Guid actor = default;
            string actorName = "";
            if (action is not null)
            {
                var match = context.Request.Headers.IfMatch;
                if (match.Count == 0) return Problem(428, "If-Match is required");
                if (match.Count != 1 || match[0] is not { Length: >= 3 } tag || tag[0] != '"' || tag[^1] != '"' ||
                    !TryRevision(tag[1..^1], out expected))
                    return Problem(400, "If-Match must contain one current strong sign-off ETag");
                if (!context.Request.HasJsonContentType()) return Problem(415, "A JSON request content type is required");
                if (context.Request.ContentLength > MaximumRequestBytes) return Problem(413, "Sign-off request exceeds its byte limit");
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                    feature.MaxRequestBodySize = MaximumRequestBytes;
                try
                {
                    request = await context.Request.ReadFromJsonAsync(SignOffJsonContext.Default.SignOffRequest, token);
                }
                catch (JsonException) { return Problem(400, "Supply only a required sign-off reason"); }
                catch (BadHttpRequestException error) when (error.StatusCode == 413)
                { return Problem(413, "Sign-off request exceeds its byte limit"); }
                if (request is null || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000 ||
                    request.Reason.Contains('\0'))
                    return Problem(400, "A nonblank reason of at most 2000 characters is required");
                if (!Guid.TryParse(context.User.FindFirstValue("oid"), out actor) || actor == Guid.Empty)
                    return Problem(403, "An authenticated principal object ID is required");
                actorName = context.User.FindFirstValue("name")?.Trim() ?? "";
                if (actorName.Length == 0) actorName = actor.ToString("D");
                if (actorName.Length > 512 || actorName.Any(char.IsControl))
                    return Problem(400, "The authenticated principal name is invalid");
            }
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(
                action is null ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted, token);
            string workflow;
            Guid version;
            bool terminal;
            await using (var query = new NpgsqlCommand("""
                SELECT workflow_id,version_id,status BETWEEN 3 AND 6 AND ended_at IS NOT NULL
                FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1
                """, connection, transaction))
            {
                query.Parameters.AddWithValue(runId);
                await using var reader = await query.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return Problem(404, "Run not found");
                workflow = reader.GetString(0);
                version = reader.GetGuid(1);
                terminal = reader.GetBoolean(2);
            }
            if (reportId is { } report)
            {
                await using var query = new NpgsqlCommand("""
                    SELECT EXISTS(SELECT 1 FROM reports
                    WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND id=$2)
                    """, connection, transaction);
                query.Parameters.AddWithValue(runId);
                query.Parameters.AddWithValue(report);
                if (await query.ExecuteScalarAsync(token) is not true) return Problem(404, "Report not found in this run");
            }
            var kind = (short)(reportId is null ? 0 : 1);
            var target = reportId ?? runId;
            if (action is not null)
            {
                if (!terminal) return Problem(409, "Only a completed run can be signed off or revoked");
                await using (var insert = Command("""
                    INSERT INTO run_signoffs(run_id,target_kind,target_id) VALUES($1,$2,$3) ON CONFLICT DO NOTHING
                    """))
                    await insert.ExecuteNonQueryAsync(token);
            }
            long revision = 0;
            await using (var head = Command("SELECT revision FROM run_signoffs WHERE run_id=$1 AND target_kind=$2 AND target_id=$3" +
                (action is null ? "" : " FOR UPDATE")))
            {
                if (await head.ExecuteScalarAsync(token) is long value) revision = value;
            }
            var history = new List<SignOffEvent>();
            await using (var query = Command("""
                SELECT revision,action,reason,actor_id,actor_name,at FROM run_signoff_events
                WHERE run_id=$1 AND target_kind=$2 AND target_id=$3
                    AND ($4::bigint IS NULL OR revision<$4 OR revision=$5)
                ORDER BY revision DESC LIMIT $6
                """))
            {
                query.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, (object?)before ?? DBNull.Value);
                query.Parameters.AddWithValue(revision);
                query.Parameters.AddWithValue(limit + 2);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    history.Add(new(reader.GetInt64(0).ToString(CultureInfo.InvariantCulture), (SignOffAction)reader.GetInt16(1),
                        reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
            }
            var latest = history.FirstOrDefault();
            if ((revision == 0) != (latest is null) || latest is not null && latest.Revision != revision.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Sign-off revision and history do not agree.");
            var signed = latest?.Action == SignOffAction.SignOff;
            if (action is not null)
            {
                if (revision != expected) return Problem(412, "Sign-off changed; reload before submitting");
                if (signed == (action == SignOffAction.SignOff))
                    return Problem(409, signed ? "This target is already signed off" : "This target has no active sign-off");
                revision = checked(revision + 1);
                latest = new(revision.ToString(CultureInfo.InvariantCulture), action.Value, request!.Reason.Trim(),
                    actor, actorName, time.GetUtcNow());
                await using (var insert = Command("""
                    INSERT INTO run_signoff_events(run_id,target_kind,target_id,revision,action,reason,actor_id,actor_name,at)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
                    """))
                {
                    insert.Parameters.AddWithValue(revision);
                    insert.Parameters.AddWithValue((short)action.Value);
                    insert.Parameters.AddWithValue(latest.Reason);
                    insert.Parameters.AddWithValue(actor);
                    insert.Parameters.AddWithValue(actorName);
                    insert.Parameters.AddWithValue(latest.At);
                    await insert.ExecuteNonQueryAsync(token);
                }
                await using (var update = Command("UPDATE run_signoffs SET revision=$4 WHERE run_id=$1 AND target_kind=$2 AND target_id=$3"))
                {
                    update.Parameters.AddWithValue(revision);
                    await update.ExecuteNonQueryAsync(token);
                }
                await using (var audit = new NpgsqlCommand("""
                    INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,team_id,summary)
                    VALUES($1,$2,$3,$4,'Run',$5,$6,jsonb_build_object('runId',$7::uuid,'reportId',$8::uuid,
                        'revision',$9::bigint,'versionId',$10::uuid))
                    """, connection, transaction))
                {
                    audit.Parameters.AddWithValue(latest.At);
                    audit.Parameters.AddWithValue(actor.ToString("D"));
                    audit.Parameters.AddWithValue(context.User.HasClaim("idtyp", "app") ? "App" : "User");
                    audit.Parameters.AddWithValue((reportId is null ? "run" : "report") +
                        (action == SignOffAction.SignOff ? ".signoff" : ".signoff.revoke"));
                    audit.Parameters.AddWithValue(runId.ToString("D"));
                    audit.Parameters.AddWithValue(workflow[..workflow.IndexOf('/')]);
                    audit.Parameters.AddWithValue(runId);
                    audit.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)reportId ?? DBNull.Value);
                    audit.Parameters.AddWithValue(revision);
                    audit.Parameters.AddWithValue(version);
                    await audit.ExecuteNonQueryAsync(token);
                }
                history.Insert(0, latest);
            }
            await transaction.CommitAsync(token);
            var page = history.Where(item => before is null || long.Parse(item.Revision, CultureInfo.InvariantCulture) < before).ToArray();
            var next = page.Length > limit ? page[limit - 1].Revision : null;
            var state = new SignOffState(revision.ToString(CultureInfo.InvariantCulture),
                latest is null ? SignOffStatus.NotSignedOff :
                    latest.Action == SignOffAction.SignOff ? SignOffStatus.SignedOff : SignOffStatus.Revoked, latest);
            context.Response.Headers.ETag = $"\"{state.Revision}\"";
            return Results.Json(new SignOffResponse(runId, reportId, workflow, version, terminal, state,
                page.Take(limit).ToArray(), next), SignOffJsonContext.Default.SignOffResponse);

            NpgsqlCommand Command(string sql)
            {
                var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue(runId);
                command.Parameters.AddWithValue(kind);
                command.Parameters.AddWithValue(target);
                return command;
            }
        }
        catch (Exception error) when (error is NpgsqlException or InvalidDataException or OverflowException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Wf.SignOff")
                .LogError(error, "Sign-off storage is unavailable or inconsistent for run {RunId}.", runId);
            return Problem(503, "Sign-off storage is unavailable");
        }
    }

    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}
