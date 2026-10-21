using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Admin;

public sealed record DeploymentOperationRequest(string OperationId, string Mode, string Target);
public sealed record DeploymentEventRequest(int Sequence, string Phase, string? Subject, string Outcome, string Description);
public sealed record DeploymentActivityEvent(int Sequence, DateTimeOffset At, string Phase, string? Subject, string Outcome, string Description);
public sealed record DeploymentOperation(string Id, string Mode, string Target, string Actor, string ActorKind,
    DateTimeOffset StartedAt, DateTimeOffset LastReportedAt, string Status, int Sequence, IReadOnlyList<DeploymentActivityEvent> Events);
public sealed record DeploymentActivityHistory(DateTimeOffset ObservedAt, int UnconfirmedAfterSeconds, IReadOnlyList<DeploymentOperation> Operations);
public sealed record DeploymentActivityAudit(int BeforeSequence, int AfterSequence, string Status, DeploymentActivityEvent? Event);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DeploymentOperationRequest))]
[JsonSerializable(typeof(DeploymentEventRequest))]
[JsonSerializable(typeof(DeploymentOperation))]
[JsonSerializable(typeof(DeploymentActivityHistory))]
[JsonSerializable(typeof(DeploymentActivityAudit))]
public partial class DeploymentActivityJson : JsonSerializerContext;

public static class DeploymentActivityEndpoints
{
    private const string Columns = "id,mode,target,actor,actor_kind,started_at,last_reported_at,status,sequence";
    private const int MaximumRequestBytes = 8 * 1024;
    public static IEndpointRouteBuilder MapDeploymentActivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/deployment-activity").RequireAuthorization(AuthExtensions.Admin);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Query.Count != 0) return Problem(400, "Deployment activity does not accept query parameters");
            try { return await next(invocation); }
            catch (NpgsqlException error)
            {
                context.RequestServices.GetRequiredService<ILogger<DeploymentOperation>>()
                    .LogError(error, "Deployment activity storage is unavailable.");
                return Problem(503, "Deployment activity storage unavailable");
            }
        });
        group.MapGet("", History).Produces<DeploymentActivityHistory>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(503)
            .WithDescription("Global Admin recent 20 reported Stage/Release operations and their ordered events. " +
                "Script-reported history is separate from Kubernetes observations. No records means no recorded history, " +
                "not no deployments. A silent reporter leaves an unconfirmed Running operation, never synthetic success.");
        Describe(group.MapPost("", Start).Accepts<DeploymentOperationRequest>("*/*"));
        Describe(group.MapPost("/{id}/events", Append).Accepts<DeploymentEventRequest>("*/*"));
        return endpoints;
    }

    private static void Describe(RouteHandlerBuilder route) =>
        route.Produces<DeploymentOperation>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503)
            .WithDescription("Global Admin deployment-script reporting, not deployment execution. Required valid principal " +
                "object ID. Start uses a caller-generated canonical lowercase UUID operationId for idempotency; " +
                "events require the next contiguous sequence (1..128). Exact retries are idempotent; changed retries " +
                "or gaps are 409. Only the initiating principal may append. Complete/Succeeded and Failed/Failed are " +
                "terminal; later events are rejected. All changes and audit records commit atomically. Strict JSON, " +
                "8 KiB body, bounded plain text; do not report secrets, command output or environment values.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                var body = operation.RequestBody?.Content?["*/*"]
                    ?? throw new InvalidOperationException("Deployment activity request schema is missing.");
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true, Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = body }
                };
                return Task.CompletedTask;
            });

    private static async Task<IResult> History(HttpContext context, NpgsqlDataSource source, TimeProvider time)
    {
        var token = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var operations = new List<DeploymentOperation>();
        await using (var command = new NpgsqlCommand($"SELECT {Columns} FROM deployment_operations ORDER BY started_at DESC,id DESC LIMIT 20", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) operations.Add(Head(reader));
        for (var index = 0; index < operations.Count; index++)
            operations[index] = operations[index] with { Events = await Events(connection, Guid.Parse(operations[index].Id), token) };
        await transaction.CommitAsync(token);
        return Results.Json(new DeploymentActivityHistory(time.GetUtcNow(), 600, operations),
            DeploymentActivityJson.Default.DeploymentActivityHistory);
    }

    private static async Task<IResult> Start(HttpContext context, NpgsqlDataSource source, TimeProvider time)
    {
        if (!Actor(context, out var actor, out var kind)) return Problem(403, "A valid audit principal is required");
        var (request, error) = await Body(context, DeploymentActivityJson.Default.DeploymentOperationRequest);
        if (error is not null) return error;
        if (request is null || !Identity(request.OperationId, out var id) || request.Mode is not ("Stage" or "Release") ||
            !Regex.IsMatch(request.Target, @"\A[a-z0-9][a-z0-9_.-]{0,100}\z", RegexOptions.CultureInvariant))
            return Problem(400, "Supply a canonical operationId, Stage or Release mode, and a valid immutable image tag");
        var token = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var now = time.GetUtcNow();
        bool created;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO deployment_operations(id,mode,target,actor,actor_kind,started_at,last_reported_at)
            VALUES($1,$2,$3,$4,$5,$6,$6) ON CONFLICT DO NOTHING RETURNING id
            """, connection))
        {
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(request.Mode);
            insert.Parameters.AddWithValue(request.Target);
            insert.Parameters.AddWithValue(actor);
            insert.Parameters.AddWithValue(kind);
            insert.Parameters.AddWithValue(now);
            created = await insert.ExecuteScalarAsync(token) is Guid;
        }
        var operation = await ReadHead(connection, id, token) ?? throw new InvalidDataException("Deployment operation disappeared.");
        if (operation.Actor != actor.ToString("D") || operation.ActorKind != kind || operation.Mode != request.Mode || operation.Target != request.Target)
            return Problem(409, "Operation identity was already used for another deployment");
        if (created) await Audit(connection, context, id, now, "deployment.start", new(0, 0, "Running", null), token);
        operation = operation with { Events = await Events(connection, id, token) };
        await transaction.CommitAsync(token);
        return Results.Json(operation, DeploymentActivityJson.Default.DeploymentOperation);
    }

    private static async Task<IResult> Append(string id, HttpContext context, NpgsqlDataSource source, TimeProvider time)
    {
        if (!Actor(context, out var actor, out var kind)) return Problem(403, "A valid audit principal is required");
        if (!Identity(id, out var operationId)) return Problem(400, "Invalid deployment operation identity");
        var (request, error) = await Body(context, DeploymentActivityJson.Default.DeploymentEventRequest);
        if (error is not null) return error;
        if (request is null || request.Sequence is < 1 or > 128 ||
            request.Phase is not ("Preflight" or "Build" or "Import" or "Configure" or "Migrate" or "Rollout" or
                "Controller" or "Drain" or "Replace" or "Resume" or "Verify" or "Complete" or "Failed") ||
            request.Outcome is not ("Running" or "Succeeded" or "Failed") ||
            !Plain(request.Description, 512) || request.Subject is not null && !Plain(request.Subject, 200) ||
            request.Phase == "Complete" && request.Outcome != "Succeeded" ||
            (request.Phase == "Failed") != (request.Outcome == "Failed"))
            return Problem(400, "Invalid deployment event sequence, phase, outcome or bounded plain text");
        var token = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var operation = await ReadHead(connection, operationId, token);
        if (operation is null) return Problem(404, "Deployment operation not found");
        if (operation.Actor != actor.ToString("D") || operation.ActorKind != kind)
            return Problem(403, "Only the initiating principal can report this operation");
        var events = await Events(connection, operationId, token);
        if (request.Sequence <= operation.Sequence)
        {
            var previous = events.SingleOrDefault(item => item.Sequence == request.Sequence);
            if (previous is null || previous.Phase != request.Phase || previous.Subject != request.Subject ||
                previous.Outcome != request.Outcome || previous.Description != request.Description)
                return Problem(409, "This sequence was already used for a different deployment event");
            await transaction.CommitAsync(token);
            return Results.Json(operation with { Events = events }, DeploymentActivityJson.Default.DeploymentOperation);
        }
        if (operation.Status != "Running" || request.Sequence != operation.Sequence + 1)
            return Problem(409, "Supply the next sequence for a nonterminal operation");
        var now = time.GetUtcNow();
        var item = new DeploymentActivityEvent(request.Sequence, now, request.Phase, request.Subject, request.Outcome, request.Description);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO deployment_activity(operation_id,sequence,at,phase,subject,outcome,description) VALUES($1,$2,$3,$4,$5,$6,$7)
            """, connection))
        {
            insert.Parameters.AddWithValue(operationId);
            insert.Parameters.AddWithValue(item.Sequence);
            insert.Parameters.AddWithValue(item.At);
            insert.Parameters.AddWithValue(item.Phase);
            insert.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)item.Subject ?? DBNull.Value);
            insert.Parameters.AddWithValue(item.Outcome);
            insert.Parameters.AddWithValue(item.Description);
            await insert.ExecuteNonQueryAsync(token);
        }
        var status = request.Phase == "Complete" ? "Succeeded" : request.Phase == "Failed" ? "Failed" : "Running";
        await using (var update = new NpgsqlCommand("""
            UPDATE deployment_operations SET sequence=$2,last_reported_at=$3,status=$4 WHERE id=$1
            """, connection))
        {
            update.Parameters.AddWithValue(operationId);
            update.Parameters.AddWithValue(item.Sequence);
            update.Parameters.AddWithValue(now);
            update.Parameters.AddWithValue(status);
            await update.ExecuteNonQueryAsync(token);
        }
        await Audit(connection, context, operationId, now, "deployment.report", new(operation.Sequence, item.Sequence, status, item), token);
        operation = (await ReadHead(connection, operationId, token))! with { Events = await Events(connection, operationId, token) };
        await transaction.CommitAsync(token);
        return Results.Json(operation, DeploymentActivityJson.Default.DeploymentOperation);
    }

    private static async Task Audit(NpgsqlConnection connection, HttpContext context, Guid id, DateTimeOffset now,
        string action, DeploymentActivityAudit summary, CancellationToken token)
    {
        await using (var partition = new NpgsqlCommand("SELECT ensure_audit_partition($1)", connection))
        {
            partition.Parameters.AddWithValue(DateOnly.FromDateTime(now.UtcDateTime));
            await partition.ExecuteNonQueryAsync(token);
        }
        Actor(context, out var actor, out var kind);
        await using var command = new NpgsqlCommand("""
            INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,$2,$3,$4,'Deployment',$5,$6)
            """, connection);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor.ToString("D"));
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(action);
        command.Parameters.AddWithValue(id.ToString("D"));
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(summary, DeploymentActivityJson.Default.DeploymentActivityAudit));
        await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<DeploymentOperation?> ReadHead(NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM deployment_operations WHERE id=$1 FOR UPDATE", connection);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Head(reader) : null;
    }
    private static DeploymentOperation Head(NpgsqlDataReader reader) => new(reader.GetGuid(0).ToString("D"), reader.GetString(1),
        reader.GetString(2), reader.GetGuid(3).ToString("D"), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7), reader.GetInt32(8), []);
    private static async Task<List<DeploymentActivityEvent>> Events(NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT sequence,at,phase,subject,outcome,description FROM deployment_activity WHERE operation_id=$1 ORDER BY sequence
            """, connection);
        command.Parameters.AddWithValue(id);
        var result = new List<DeploymentActivityEvent>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }
    private static async Task<(T? Value, IResult? Error)> Body<T>(HttpContext context, JsonTypeInfo<T> type) where T : class
    {
        if (!context.Request.HasJsonContentType()) return (null, Problem(415, "JSON content is required"));
        if (context.Request.ContentLength > MaximumRequestBytes) return (null, Problem(413, "Deployment activity exceeds 8 KiB"));
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaximumRequestBytes;
        try { return (await context.Request.ReadFromJsonAsync(type, context.RequestAborted), null); }
        catch (JsonException) { return (null, Problem(400, "Supply exactly the documented deployment activity fields and value types")); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return (null, Problem(413, "Deployment activity exceeds 8 KiB")); }
    }
    private static bool Actor(HttpContext context, out Guid actor, out string kind)
    {
        kind = context.User.HasClaim("idtyp", "app") ? "App" : "User";
        return Guid.TryParseExact(context.User.FindFirstValue("oid"), "D", out actor) && actor != Guid.Empty;
    }
    private static bool Identity(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) &&
        id != Guid.Empty && value == id.ToString("D");
    private static bool Plain(string value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}
