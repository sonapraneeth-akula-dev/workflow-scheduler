using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Admin;

public sealed record SystemNotice(string Message, string Severity, DateTimeOffset? ExpiresAt)
{
    public SystemNotice Validate(DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(Message) || Message.Length > 2000 ||
            Message.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
            Severity is not ("Info" or "Warning" or "Critical"))
            throw new ArgumentException("Supply a nonblank plain-text message of at most 2000 characters and Info, Warning or Critical severity.");
        if (now is { } at && ExpiresAt <= at)
            throw new ArgumentException("Expiry must be in the future, or null for no expiry.");
        return this;
    }
}

public sealed record SystemNoticeResponse(string Revision, SystemNotice? Notice, bool CanManage);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SystemNotice))]
[JsonSerializable(typeof(SystemNoticeResponse))]
public partial class SystemNoticeJson : JsonSerializerContext;

public static class SystemNoticeEndpoints
{
    private const int MaximumRequestBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapSystemNoticeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var read = endpoints.MapGroup("/api/v1/system-notice").RequireAuthorization();
        var write = endpoints.MapGroup("/api/v1/admin/system-notice").RequireAuthorization(AuthExtensions.Admin);
        foreach (var group in new[] { read, write })
            group.AddEndpointFilter(async (invocation, next) =>
            {
                var context = invocation.HttpContext;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                if (context.Request.Query.Count != 0) return Invalid("System notices do not accept query parameters.");
                try { return await next(invocation); }
                catch (NpgsqlException error)
                {
                    context.RequestServices.GetRequiredService<ILogger<SystemNoticeResponse>>()
                        .LogError(error, "System notice storage is unavailable.");
                    return Problem(503, "System notice unavailable");
                }
                catch (Exception error) when (error is InvalidDataException or InvalidCastException)
                {
                    context.RequestServices.GetRequiredService<ILogger<SystemNoticeResponse>>()
                        .LogError(error, "Committed system notice is inconsistent.");
                    return Problem(500, "Committed system notice is inconsistent");
                }
            });
        read.MapGet("", Read).Produces<SystemNoticeResponse>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Authenticated global notice. Expired or cleared notices return notice:null, not an all-clear " +
                "health signal. canManage reflects global Admin only. Includes a strong ETag for edits; no-store.");
        var publish = write.MapPut("", Publish).Accepts<SystemNotice>("*/*")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                var body = operation.RequestBody?.Content?["*/*"]
                    ?? throw new InvalidOperationException("System notice request schema is missing.");
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true, Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = body }
                };
                return Task.CompletedTask;
            });
        foreach (var route in new[] { publish, write.MapDelete("", Clear) })
        {
            route.Produces<SystemNoticeResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
                .ProducesProblem(412).ProducesProblem(413).ProducesProblem(415).ProducesProblem(428)
                .ProducesProblem(500).ProducesProblem(503)
                .WithDescription("Global Admin only. Atomically audited replacement or clear. Requires the exact strong " +
                    "If-Match ETag from GET; 412 preserves concurrent edits. One plain-text message (1-2000 characters), " +
                    "Info/Warning/Critical severity, optional future expiresAt (null means no expiry). No HTML, scheduling " +
                    "or automatic incident detection. PUT requires all three fields and at most 16 KiB JSON; DELETE has no body.")
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "If-Match", In = ParameterLocation.Header, Required = true,
                        Description = "Exact strong ETag from GET, including quotes (initially \"0\").",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                    return Task.CompletedTask;
                });
        }
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, NpgsqlDataSource source, TimeProvider time)
    {
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        var (revision, notice) = await ReadStored(connection, false, context.RequestAborted);
        return Respond(context, revision, notice, time.GetUtcNow());
    }

    private static Task<IResult> Publish(HttpContext context, NpgsqlDataSource source, TimeProvider time) =>
        Change(context, source, time, false);
    private static Task<IResult> Clear(HttpContext context, NpgsqlDataSource source, TimeProvider time) =>
        Change(context, source, time, true);

    private static async Task<IResult> Change(HttpContext context, NpgsqlDataSource source, TimeProvider time, bool clear)
    {
        if (!Guid.TryParseExact(context.User.FindFirstValue("oid"), "D", out var actor) || actor == Guid.Empty)
            return Problem(403, "A valid audit principal is required");
        var match = context.Request.Headers.IfMatch;
        if (match.Count == 0) return Problem(428, "If-Match is required");
        if (match.Count != 1 || match[0] is not { Length: >= 3 } tag || tag[0] != '"' || tag[^1] != '"' ||
            !SignOffEndpoints.TryRevision(tag[1..^1], out var expected))
            return Invalid("Supply exactly one current strong system notice ETag.");
        SystemNotice? notice = null;
        if (clear)
        {
            if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
                return Invalid("Clear does not accept a request body.");
        }
        else
        {
            if (!context.Request.HasJsonContentType()) return Problem(415, "JSON content is required");
            if (context.Request.ContentLength > MaximumRequestBytes) return TooLarge();
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaximumRequestBytes;
            try
            {
                notice = (await context.Request.ReadFromJsonAsync(SystemNoticeJson.Default.SystemNotice, context.RequestAborted)
                    ?? throw new ArgumentException("Supply a notice object.")).Validate(time.GetUtcNow());
            }
            catch (JsonException) { return Invalid("Supply exactly message, severity and expiresAt with documented value types."); }
            catch (ArgumentException error) { return Invalid(error.Message); }
            catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
        }
        var token = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var (revision, before) = await ReadStored(connection, true, token);
        if (revision != expected) return Problem(412, "The system notice changed; reload before publishing or clearing");
        var now = time.GetUtcNow();
        try { notice?.Validate(now); }
        catch (ArgumentException error) { return Invalid(error.Message); }
        await using (var update = new NpgsqlCommand("""
            UPDATE system_notice SET revision=revision+1,message=$1,severity=$2,expires_at=$3 WHERE singleton RETURNING revision
            """, connection))
        {
            update.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)notice?.Message ?? DBNull.Value);
            update.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)notice?.Severity ?? DBNull.Value);
            update.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)notice?.ExpiresAt?.ToUniversalTime() ?? DBNull.Value);
            revision = (long)(await update.ExecuteScalarAsync(token) ?? throw new InvalidDataException("System notice singleton is missing."));
        }
        await using (var partition = new NpgsqlCommand("SELECT ensure_audit_partition($1)", connection))
        {
            partition.Parameters.AddWithValue(DateOnly.FromDateTime(now.UtcDateTime));
            await partition.ExecuteNonQueryAsync(token);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,$2,$3,$4,'SystemNotice','global',
                jsonb_build_object('before',$5::jsonb,'after',$6::jsonb,'revision',$7))
            """, connection))
        {
            audit.Parameters.AddWithValue(now);
            audit.Parameters.AddWithValue(actor.ToString("D"));
            audit.Parameters.AddWithValue(context.User.HasClaim("idtyp", "app") ? "App" : "User");
            audit.Parameters.AddWithValue(clear ? "system_notice.clear" : "system_notice.publish");
            audit.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(before, SystemNoticeJson.Default.SystemNotice));
            audit.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(notice, SystemNoticeJson.Default.SystemNotice));
            audit.Parameters.AddWithValue(revision);
            await audit.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return Respond(context, revision, notice, now);
    }

    private static async Task<(long Revision, SystemNotice? Notice)> ReadStored(
        NpgsqlConnection connection, bool forUpdate, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT revision,message,severity,expires_at FROM system_notice WHERE singleton" +
            (forUpdate ? " FOR UPDATE" : ""), connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("System notice singleton is missing.");
        var revision = reader.GetInt64(0);
        SystemNotice? notice = reader.IsDBNull(1) ? null : new(reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
        try { notice?.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("Stored system notice is invalid.", error); }
        return (revision, notice);
    }

    private static IResult Respond(HttpContext context, long revision, SystemNotice? notice, DateTimeOffset now)
    {
        var value = revision.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.ETag = $"\"{value}\"";
        return Results.Json(new SystemNoticeResponse(value,
            notice?.ExpiresAt <= now ? null : notice, context.User.IsInRole(AuthExtensions.Admin)),
            SystemNoticeJson.Default.SystemNoticeResponse);
    }

    private static IResult Invalid(string detail) => Results.Problem(statusCode: 400, title: "Invalid system notice", detail: detail);
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
    private static IResult TooLarge() => Problem(413, "System notice exceeds 16 KiB");
}
