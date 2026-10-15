using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Registry;
using Wf.Validation;
using Wf.Validation.Schedules;

namespace Wf.ControlPlane.Api;

public static class UserSchedulesEndpoints
{
    private const long MaximumRequestBytes = 1024 * 1024;

    public static IServiceCollection AddUserSchedules(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(UserScheduleClient.FromConfiguration(configuration));
        return services;
    }

    public static IEndpointRouteBuilder MapUserSchedulesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/schedules/{id:guid}", Get).RequireAuthorization()
            .Produces<UserScheduleResponse>().ProducesProblem(403).ProducesProblem(404);
        var ui = endpoints.MapGroup("/api/v1/schedules").WithGroupName("ui").RequireAuthorization()
            .WithMetadata(new UserScheduleRoute());
        ui.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (!context.RequestServices.GetRequiredService<UserScheduleClient>().Allows(context.User))
                return Results.Problem(statusCode: 403, title: "Forbidden",
                    detail: "Schedule management requires a delegated user token issued to the configured Kardia SPA.");
            context.Response.Headers.CacheControl = "no-store";
            try { return await next(invocation); }
            catch (Rejected error) { return error.Result; }
        });
        ui.MapPost("", Create).Accepts<UserScheduleRequest>("application/json")
            .Produces<UserScheduleResponse>(201).ProducesProblem(400).ProducesProblem(403);
        ui.MapPost("/preview", Preview).Accepts<UserScheduleRequest>("application/json")
            .Produces<UserSchedulePreview>().ProducesProblem(400).ProducesProblem(403);
        ui.MapPut("/{id:guid}", Edit).Accepts<UserScheduleRequest>("application/json")
            .Produces<UserScheduleResponse>().ProducesProblem(400).ProducesProblem(403)
            .ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        ui.MapPost("/{id:guid}/pause", Pause).Produces<UserScheduleResponse>(202)
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        ui.MapPost("/{id:guid}/resume", Resume).Produces<UserScheduleResponse>(202)
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        ui.MapDelete("/{id:guid}", Delete).Produces(204)
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        return endpoints;
    }

    private static async Task<IResult> Get(HttpContext context, Guid id, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        var schedule = await ReadAsync(connection, id, false, context.RequestAborted);
        if (schedule is null) return Missing();
        if (!await AllowedAsync(context, schedule.WorkflowId, RegistryRole.Viewer))
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        context.Response.Headers.CacheControl = "no-store";
        return Response(context, schedule);
    }

    private static async Task<IResult> Preview(HttpContext context, NpgsqlDataSource source, ActiveVersionCache versions, TimeProvider time)
    {
        var request = await ReadRequestAsync(context);
        ValidateIdentity(request);
        if (!await AllowedAsync(context, request.WorkflowId, RegistryRole.Operator))
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        var definition = await ValidateAsync(request, versions, source, connection, context.RequestAborted);
        return Results.Json(new UserSchedulePreview(definition.Expression.GetNextOccurrences(Now(time))),
            UserScheduleJson.Default.UserSchedulePreview);
    }

    private static async Task<IResult> Create(HttpContext context, NpgsqlDataSource source, ActiveVersionCache versions, TimeProvider time)
    {
        var request = await ReadRequestAsync(context);
        ValidateIdentity(request);
        if (!await AllowedAsync(context, request.WorkflowId, RegistryRole.Operator))
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        if (!await LockWorkflowAsync(connection, request.WorkflowId, context.RequestAborted)) return MissingWorkflow();
        var definition = await ValidateAsync(request, versions, source, connection, context.RequestAborted);
        var now = Now(time);
        var id = Guid.CreateVersion7(now);
        var series = Guid.CreateVersion7(now);
        var actor = Guid.ParseExact(context.User.FindFirstValue("oid")!, "D").ToString("D");
        var next = definition.Expression.GetNextOccurrence(now);
        await using (var insert = Query(connection, """
            INSERT INTO schedules(id,workflow_id,origin,name,timezone,cron,interval_s,variables,version_pin,
                overlap_policy,catchup_policy,next_fire_at,created_by,updated_by,created_at,updated_at)
            VALUES($1,$2,1,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11,$12,$12,$13,$13)
            """, id, request.WorkflowId, request.Name, request.Timezone, (object?)request.Cron ?? DBNull.Value,
            Seconds(request.Interval), JsonSerializer.Serialize(definition.Variables, UserScheduleJson.Default.ImmutableDictionaryStringJsonElement),
            (object?)request.VersionPin ?? DBNull.Value, (short)request.Overlap, (short)request.CatchUp,
            (object?)next ?? DBNull.Value, actor, now))
            await insert.ExecuteNonQueryAsync(context.RequestAborted);
        await using (var insert = Query(connection, "INSERT INTO run_series(id,workflow_id,schedule_id) VALUES($1,$2,$3)",
            series, request.WorkflowId, id))
            await insert.ExecuteNonQueryAsync(context.RequestAborted);
        var schedule = new UserScheduleResponse(id, series, request.WorkflowId, request.Name, "User", request.Timezone,
            request.Cron, request.Interval, request.VersionPin, request.Overlap, request.CatchUp, definition.Variables, false, 1, next);
        await AuditAndNotifyAsync(connection, schedule, actor, "schedule.create", context.RequestAborted);
        await transaction.CommitAsync(context.RequestAborted);
        context.Response.Headers.Location = "/api/v1/schedules/" + id.ToString("D");
        return Response(context, schedule, 201);
    }

    private static Task<IResult> Edit(HttpContext context, Guid id, NpgsqlDataSource source, ActiveVersionCache versions, TimeProvider time) =>
        ChangeAsync(context, id, source, versions, time, Mutation.Edit);
    private static Task<IResult> Pause(HttpContext context, Guid id, NpgsqlDataSource source, ActiveVersionCache versions, TimeProvider time) =>
        ChangeAsync(context, id, source, versions, time, Mutation.Pause);
    private static Task<IResult> Resume(HttpContext context, Guid id, NpgsqlDataSource source, ActiveVersionCache versions, TimeProvider time) =>
        ChangeAsync(context, id, source, versions, time, Mutation.Resume);
    private static Task<IResult> Delete(HttpContext context, Guid id, NpgsqlDataSource source, ActiveVersionCache versions, TimeProvider time) =>
        ChangeAsync(context, id, source, versions, time, Mutation.Delete);

    private static async Task<IResult> ChangeAsync(HttpContext context, Guid id, NpgsqlDataSource source,
        ActiveVersionCache versions, TimeProvider time, Mutation mutation)
    {
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        var schedule = await ReadAsync(connection, id, false, context.RequestAborted);
        if (schedule is null) return Missing();
        if (!await AllowedAsync(context, schedule.WorkflowId, RegistryRole.Operator))
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        if (schedule.Origin == "Code") return CodeOwned();
        if (!context.Request.Headers.ContainsKey("If-Match"))
            return Results.Problem(statusCode: 428, title: "A current If-Match schedule ETag is required");
        if (!ScheduleEntityTag.TryParse(context.Request.Headers.IfMatch, out var expected))
            return Invalid("$.headers.If-Match", "Supply exactly one strong schedule ETag, such as \"1\".");
        if (schedule.RowVersion != expected) return Stale();
        UserScheduleRequest? request = null;
        if (mutation == Mutation.Edit)
        {
            request = await ReadRequestAsync(context);
            ValidateIdentity(request);
            if (request.WorkflowId != schedule.WorkflowId)
                return Invalid("$.workflowId", "A schedule cannot be moved to a different workflow.");
        }
        else await RequireEmptyBodyAsync(context);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        // Registration locks the workflow before its schedules; use the same order.
        if (!await LockWorkflowAsync(connection, schedule.WorkflowId, context.RequestAborted)) return MissingWorkflow();
        schedule = await ReadAsync(connection, id, true, context.RequestAborted);
        if (schedule is null) return Missing();
        if (schedule.Origin == "Code") return CodeOwned();
        if (schedule.RowVersion != expected) return Stale();
        var now = Now(time);
        var updated = schedule;
        if (request is not null)
        {
            var definition = await ValidateAsync(request, versions, source, connection, context.RequestAborted);
            var expressionChanged = (schedule.Cron, schedule.Interval, schedule.Timezone) != (request.Cron, request.Interval, request.Timezone);
            updated = schedule with
            {
                Name = request.Name, Timezone = request.Timezone, Cron = request.Cron, Interval = request.Interval,
                VersionPin = request.VersionPin, Overlap = request.Overlap, CatchUp = request.CatchUp, Variables = definition.Variables,
                NextFireAt = schedule.Paused ? null : expressionChanged ? definition.Expression.GetNextOccurrence(now) : schedule.NextFireAt
            };
        }
        else if (mutation is Mutation.Pause or Mutation.Delete)
            updated = schedule with { Paused = true, NextFireAt = null };
        else if (mutation == Mutation.Resume && schedule.Paused)
            updated = schedule with
            {
                Paused = false,
                NextFireAt = ScheduleExpression.Create(schedule.Cron, schedule.Interval, schedule.Timezone).GetNextOccurrence(now)
            };
        var status = mutation is Mutation.Pause or Mutation.Resume ? 202 : 200;
        if (mutation != Mutation.Delete && SameConfiguration(schedule, updated))
        {
            await transaction.CommitAsync(context.RequestAborted);
            return Response(context, schedule, status);
        }
        var actor = Guid.ParseExact(context.User.FindFirstValue("oid")!, "D").ToString("D");
        await using (var write = Query(connection, """
            UPDATE schedules SET name=$2,timezone=$3,cron=$4,interval_s=$5,variables=$6::jsonb,version_pin=$7,
                overlap_policy=$8,catchup_policy=$9,next_fire_at=$10,paused=$11,updated_by=$12,updated_at=$13,
                deleted_at=$14,row_version=row_version+1 WHERE id=$1 RETURNING row_version
            """, id, updated.Name, updated.Timezone, (object?)updated.Cron ?? DBNull.Value, Seconds(updated.Interval),
            JsonSerializer.Serialize(updated.Variables, UserScheduleJson.Default.ImmutableDictionaryStringJsonElement),
            (object?)updated.VersionPin ?? DBNull.Value, (short)updated.Overlap, (short)updated.CatchUp,
            (object?)updated.NextFireAt ?? DBNull.Value, updated.Paused, actor, now,
            mutation == Mutation.Delete ? now : DBNull.Value))
            updated = updated with
            {
                RowVersion = (int)(await write.ExecuteScalarAsync(context.RequestAborted)
                    ?? throw new InvalidDataException("The locked schedule disappeared during its update."))
            };
        var action = mutation switch
        {
            Mutation.Edit => "schedule.edit", Mutation.Pause => "schedule.pause",
            Mutation.Resume => "schedule.resume", Mutation.Delete => "schedule.delete",
            _ => throw new InvalidOperationException("Unsupported schedule mutation.")
        };
        await AuditAndNotifyAsync(connection, updated, actor, action, context.RequestAborted);
        await transaction.CommitAsync(context.RequestAborted);
        if (mutation != Mutation.Delete) return Response(context, updated, status);
        context.Response.Headers.ETag = ScheduleEntityTag.Format(updated.RowVersion);
        return Results.NoContent();
    }

    private static async Task<ResolvedScheduleInstance> ValidateAsync(UserScheduleRequest request, ActiveVersionCache versions,
        NpgsqlDataSource source, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Any(char.IsControl) || request.Name.EnumerateRunes().Take(129).Count() > 128)
            throw new Rejected(Invalid("$.name", "Use a nonempty schedule name of at most 128 Unicode characters without control characters."));
        if (request.Variables is null) throw new Rejected(Invalid("$.variables", "Supply a JSON object, or omit variables."));
        if (Guid.TryParseExact(request.VersionPin, "D", out var pinned) && pinned == Guid.Empty)
            throw new Rejected(Validation([RuleCatalog.ScheduleVersionPin.CreateError("schedule", "$.versionPin")]));
        SelectedWorkflowVersion selected;
        try
        {
            selected = await WorkflowVersionResolver.ResolveAsync(versions, source, request.WorkflowId, request.VersionPin,
                true, cancellationToken, connection);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new Rejected(Validation([RuleCatalog.ScheduleVersionPin.CreateError("schedule", "$.versionPin")]));
        }
        catch (KeyNotFoundException) { throw new Rejected(Results.Problem(statusCode: 404, title: "Workflow version not found")); }
        catch (InvalidOperationException) when (!versions.IsReady)
        {
            throw new Rejected(Results.Problem(statusCode: 503, title: "Workflow versions are synchronizing"));
        }
        var file = new ScheduleFile([new(request.Name, request.Timezone, request.Cron, request.Interval, request.VersionPin,
            request.Overlap, request.CatchUp, request.Variables)]);
        var validated = ScheduleFileValidator.Validate(JsonSerializer.SerializeToUtf8Bytes(file, ScheduleFileJsonContext.Default.ScheduleFile),
            selected.Manifest.Variables, "schedule", cancellationToken);
        if (!validated.IsValid)
            throw new Rejected(Validation(validated.Errors.Select(error => error with
            {
                Path = error.Path.Replace("$.instances[0].id", "$.name", StringComparison.Ordinal)
                    .Replace("$.instances[0]", "$", StringComparison.Ordinal)
            })));
        return validated.Instances.Single();
    }

    private static async Task<UserScheduleRequest> ReadRequestAsync(HttpContext context)
    {
        Limit(context, MaximumRequestBytes);
        if (!context.Request.HasJsonContentType())
            throw new Rejected(Results.Problem(statusCode: 415, title: "A JSON request content type is required"));
        try
        {
            return await context.Request.ReadFromJsonAsync(UserScheduleJson.Default.UserScheduleRequest, context.RequestAborted)
                ?? throw new Rejected(Invalid("$", "Supply a schedule request."));
        }
        catch (JsonException error) { throw new Rejected(Invalid(error.Path ?? "$", "Supply a valid schedule request with only supported properties.")); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { throw new Rejected(TooLarge()); }
    }

    private static async Task RequireEmptyBodyAsync(HttpContext context)
    {
        Limit(context, 4096);
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true) return;
        if (!context.Request.HasJsonContentType())
            throw new Rejected(Results.Problem(statusCode: 415, title: "A JSON request content type is required"));
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any())
                throw new Rejected(Invalid("$", "Omit the body or supply an empty object."));
        }
        catch (JsonException) { throw new Rejected(Invalid("$", "Omit the body or supply an empty object.")); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { throw new Rejected(TooLarge()); }
    }

    private static void Limit(HttpContext context, long bytes)
    {
        if (context.Request.ContentLength > bytes) throw new Rejected(TooLarge());
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = bytes;
    }

    private static void ValidateIdentity(UserScheduleRequest request)
    {
        if (!WorkflowIdentity.TryGetTypeScope(request.WorkflowId, out _))
            throw new Rejected(Invalid("$.workflowId", "Use an exact team/type/name identity with at most 128 Unicode characters per segment."));
    }

    private static async Task<bool> AllowedAsync(HttpContext context, string workflow, RegistryRole role)
    {
        if (!WorkflowIdentity.TryGetTypeScope(workflow, out var scope))
            throw new InvalidDataException("A stored schedule has an invalid workflow identity.");
        return (await context.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Type, scope, context.RequestAborted), TeamAuthorizationExtensions.Policy(role))).Succeeded;
    }

    private static async Task<bool> LockWorkflowAsync(NpgsqlConnection connection, string workflow, CancellationToken token)
    {
        await using var query = Query(connection, "SELECT id FROM workflows WHERE id=$1 AND deleted_at IS NULL FOR SHARE", workflow);
        return await query.ExecuteScalarAsync(token) is string;
    }

    private static async Task<UserScheduleResponse?> ReadAsync(NpgsqlConnection connection, Guid id, bool forUpdate, CancellationToken token)
    {
        await using var query = Query(connection, """
            SELECT s.id,r.id,s.workflow_id,s.name,s.origin,s.timezone,s.cron,s.interval_s,s.version_pin,
                s.overlap_policy,s.catchup_policy,s.variables::text,s.paused,s.row_version,s.next_fire_at
            FROM schedules s JOIN workflows w ON w.id=s.workflow_id LEFT JOIN run_series r ON r.schedule_id=s.id
            WHERE s.id=$1 AND s.deleted_at IS NULL AND w.deleted_at IS NULL
            """ + (forUpdate ? " FOR UPDATE OF s" : ""), id);
        await using var row = await query.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token)) return null;
        if (row.IsDBNull(1)) throw new InvalidDataException("The schedule has no permanent run series.");
        var origin = row.GetInt16(4) switch { 0 => "Code", 1 => "User", _ => throw new InvalidDataException("The schedule origin is invalid.") };
        return new(row.GetGuid(0), row.GetGuid(1), row.GetString(2), row.GetString(3), origin, row.GetString(5),
            row.IsDBNull(6) ? null : row.GetString(6), row.IsDBNull(7) ? null : TimeSpan.FromSeconds(row.GetInt32(7)),
            row.IsDBNull(8) ? null : row.GetString(8), (ScheduleOverlapPolicy)row.GetInt16(9), (ScheduleCatchUpPolicy)row.GetInt16(10),
            JsonSerializer.Deserialize(row.GetString(11), UserScheduleJson.Default.ImmutableDictionaryStringJsonElement)
                ?? throw new InvalidDataException("Schedule variables are null."),
            row.GetBoolean(12), row.GetInt32(13), row.IsDBNull(14) ? null : row.GetFieldValue<DateTimeOffset>(14));
    }

    private static async Task AuditAndNotifyAsync(NpgsqlConnection connection, UserScheduleResponse schedule, string actor,
        string action, CancellationToken token)
    {
        await using var command = Query(connection, """
            WITH logged AS (
                INSERT INTO audit_log(actor,actor_kind,action,target_kind,target_id,team_id,summary)
                VALUES($1,'User',$2,'Schedule',$3,$4,jsonb_build_object('workflowId',$5,'rowVersion',$6,'origin','User'))
                RETURNING target_id
            )
            SELECT pg_notify('schedules',$5) FROM logged
            """, actor, action, schedule.Id.ToString("D"), schedule.WorkflowId[..schedule.WorkflowId.IndexOf('/')],
            schedule.WorkflowId, schedule.RowVersion);
        await command.ExecuteNonQueryAsync(token);
    }

    private static bool SameConfiguration(UserScheduleResponse first, UserScheduleResponse second) =>
        first with { Variables = second.Variables } == second &&
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first.Variables, UserScheduleJson.Default.ImmutableDictionaryStringJsonElement),
            JsonSerializer.SerializeToElement(second.Variables, UserScheduleJson.Default.ImmutableDictionaryStringJsonElement));

    private static NpgsqlCommand Query(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return command;
    }

    private static object Seconds(TimeSpan? interval) =>
        interval is { } value ? checked((int)(value.Ticks / TimeSpan.TicksPerSecond)) : DBNull.Value;
    private static DateTimeOffset Now(TimeProvider time) => new(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
    private static IResult Response(HttpContext context, UserScheduleResponse schedule, int status = 200)
    {
        context.Response.Headers.ETag = ScheduleEntityTag.Format(schedule.RowVersion);
        return Results.Json(schedule, UserScheduleJson.Default.UserScheduleResponse, statusCode: status);
    }
    private static IResult Validation(IEnumerable<ValidationError> errors) => Results.Problem(statusCode: 400,
        title: "Schedule validation failed", extensions: new Dictionary<string, object?> { ["errors"] = errors });
    private static IResult Invalid(string path, string hint) => Validation([new("SCH001", "schedule", path,
        "The schedule request does not match its contract.", hint)]);
    private static IResult Missing() => Results.Problem(statusCode: 404, title: "Schedule not found");
    private static IResult MissingWorkflow() => Results.Problem(statusCode: 404, title: "Workflow not found");
    private static IResult Stale() => Results.Problem(statusCode: 412, title: "The schedule changed; reload its current ETag");
    private static IResult CodeOwned() => Results.Problem(statusCode: 409, title: "Code-defined schedules are read-only",
        detail: "Change and register the source schedule file instead.");
    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "Schedule request exceeds its byte limit");
    private sealed class Rejected(IResult result) : Exception("The schedule request was rejected.") { public IResult Result => result; }
    private enum Mutation { Edit, Pause, Resume, Delete }
}
