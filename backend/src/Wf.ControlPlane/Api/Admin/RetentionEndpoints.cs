using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Admin;

public sealed record RetentionPolicy(
    [property: JsonPropertyName("default_days")] int DefaultDays,
    [property: JsonPropertyName("audit_days")] int AuditDays,
    [property: JsonPropertyName("per_type")] Dictionary<string, int> PerType)
{
    public RetentionPolicy Validate()
    {
        if (DefaultDays is < 1 or > 36500 || AuditDays is < 365 or > 36500 ||
            PerType is null || PerType.Count > 256 ||
            PerType.Any(pair => !WorkflowIdentity.TryGetTypeScope(pair.Key + "/workflow", out _) ||
                pair.Value is < 1 or > 36500))
            throw new ArgumentException("Use default_days and per_type days from 1 to 36500, audit_days from 365 to 36500, " +
                "and at most 256 exact team/type overrides.");
        return this;
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RetentionPolicy))]
public partial class RetentionJson : JsonSerializerContext;

public static class RetentionEndpoints
{
    private const int MaximumRequestBytes = 64 * 1024;
    private const string ReadSql = """
        SELECT c.default_days,c.audit_days,
            coalesce((SELECT jsonb_object_agg(id,retention_days ORDER BY id)
                FROM workflow_types WHERE retention_days IS NOT NULL),'{}'::jsonb)::text
        FROM retention_config c WHERE singleton
        """;

    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/retention").RequireAuthorization(AuthExtensions.Admin)
            .WithMetadata(new ResponseCacheAttribute { NoStore = true });
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!Guid.TryParseExact(context.User.FindFirstValue("oid"), "D", out var actor) || actor == Guid.Empty)
                return Results.Problem(statusCode: 403, title: "A valid audit principal is required");
            if (context.Request.Query.Count != 0) return Invalid("Retention does not accept query parameters.");
            try { return await next(invocation); }
            catch (NpgsqlException error)
            {
                context.RequestServices.GetRequiredService<ILogger<RetentionPolicy>>()
                    .LogError(error, "Retention configuration storage is unavailable.");
                return Results.Problem(statusCode: 503, title: "Retention configuration unavailable");
            }
            catch (Exception error) when (error is InvalidDataException or InvalidCastException or JsonException)
            {
                context.RequestServices.GetRequiredService<ILogger<RetentionPolicy>>()
                    .LogError(error, "Committed retention configuration is inconsistent.");
                return Results.Problem(statusCode: 500, title: "Committed retention configuration is inconsistent");
            }
        });
        group.MapGet("", Read).Produces<RetentionPolicy>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Global Admin retention configuration. default_days starts at 90; audit_days starts at 365 " +
                "and cannot be less than one year. per_type maps exact registered team/type IDs to run retention days. " +
                "Run-owned events, logs, reports, artifacts and metrics follow the run policy; audit is independent.");
        group.MapPut("", Write).Accepts<RetentionPolicy>("*/*")
            .Produces<RetentionPolicy>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Global Admin full replacement, atomically audited with before/after configuration. " +
                "Omitted per_type entries revert to the default. Requires all three properties, at most 256 registered " +
                "type overrides and a 64 KiB JSON body. Days are integers 1..36500; audit_days is 365..36500. " +
                "Concurrent writes serialize; the last committed replacement wins. This endpoint configures policy, not an immediate purge.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                var body = operation.RequestBody?.Content?["*/*"]
                    ?? throw new InvalidOperationException("Retention request schema is missing.");
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true, Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = body }
                };
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        return Results.Json(await ReadPolicy(connection, context.RequestAborted), RetentionJson.Default.RetentionPolicy);
    }

    private static async Task<RetentionPolicy> ReadPolicy(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(ReadSql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Retention singleton is missing.");
        var perType = JsonSerializer.Deserialize(reader.GetString(2), RetentionJson.Default.DictionaryStringInt32)
            ?? throw new InvalidDataException("Retention overrides are missing.");
        var policy = new RetentionPolicy(reader.GetInt32(0), reader.GetInt32(1), perType);
        try { return policy.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid persisted retention policy.", error); }
    }

    private static async Task<IResult> Write(HttpContext context, NpgsqlDataSource source, TimeProvider time)
    {
        if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 415, title: "JSON content is required");
        if (context.Request.ContentLength > MaximumRequestBytes) return TooLarge();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaximumRequestBytes;
        RetentionPolicy policy;
        try
        {
            policy = (await context.Request.ReadFromJsonAsync(RetentionJson.Default.RetentionPolicy, context.RequestAborted)
                ?? throw new ArgumentException("Supply a retention configuration object.")).Validate();
        }
        catch (JsonException) { return Invalid("Supply exactly default_days, audit_days and per_type with documented value types."); }
        catch (ArgumentException error) { return Invalid(error.Message); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
        var token = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var mutex = new NpgsqlCommand("SELECT singleton FROM retention_config WHERE singleton FOR UPDATE", connection))
            if (await mutex.ExecuteScalarAsync(token) is not true) throw new InvalidDataException("Retention singleton is missing.");
        var before = await ReadPolicy(connection, token);
        await using (var types = new NpgsqlCommand("SELECT id FROM workflow_types WHERE id=ANY($1) ORDER BY id FOR UPDATE", connection))
        {
            types.Parameters.AddWithValue(policy.PerType.Keys.ToArray());
            var count = 0;
            await using (var reader = await types.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) count++;
            if (count != policy.PerType.Count) return Invalid("Every per_type override must name an existing workflow type.");
        }
        await using (var update = new NpgsqlCommand(
            "UPDATE retention_config SET default_days=$1,audit_days=$2 WHERE singleton", connection))
        {
            update.Parameters.AddWithValue(policy.DefaultDays);
            update.Parameters.AddWithValue(policy.AuditDays);
            await update.ExecuteNonQueryAsync(token);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE workflow_types SET retention_days=($1::jsonb ->> id)::int
            WHERE retention_days IS NOT NULL OR $1::jsonb ? id
            """, connection))
        {
            update.Parameters.AddWithValue(NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(policy.PerType, RetentionJson.Default.DictionaryStringInt32));
            await update.ExecuteNonQueryAsync(token);
        }
        var now = time.GetUtcNow();
        await using (var partition = new NpgsqlCommand("SELECT ensure_audit_partition($1)", connection))
        {
            partition.Parameters.AddWithValue(DateOnly.FromDateTime(now.UtcDateTime));
            await partition.ExecuteNonQueryAsync(token);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,$2,$3,'retention.update','Retention','global',jsonb_build_object('before',$4::jsonb,'after',$5::jsonb))
            """, connection))
        {
            audit.Parameters.AddWithValue(now);
            audit.Parameters.AddWithValue(Guid.ParseExact(context.User.FindFirstValue("oid")!, "D").ToString("D"));
            audit.Parameters.AddWithValue(context.User.HasClaim("idtyp", "app") ? "App" : "User");
            audit.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(before, RetentionJson.Default.RetentionPolicy));
            audit.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(policy, RetentionJson.Default.RetentionPolicy));
            await audit.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return Results.Json(policy, RetentionJson.Default.RetentionPolicy);
    }

    private static IResult Invalid(string detail) => Results.Problem(statusCode: 400, title: "Invalid retention configuration", detail: detail);
    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "Retention configuration exceeds 64 KiB");
}
