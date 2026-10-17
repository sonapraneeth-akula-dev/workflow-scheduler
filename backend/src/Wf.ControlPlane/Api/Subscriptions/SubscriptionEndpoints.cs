using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Subscriptions;

public static class SubscriptionEndpoints
{
    private const long MaximumRequestBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/subscriptions").RequireAuthorization()
            .WithMetadata(new ResponseCacheAttribute { NoStore = true });
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.User.HasClaim("idtyp", "app") || context.User.IsInRole(AuthExtensions.Agent) ||
                context.User.IsInRole(AuthExtensions.Registrar) ||
                !Guid.TryParseExact(context.User.FindFirstValue("oid"), "D", out var actor) || actor == Guid.Empty)
                return Denied();
            try { return await next(invocation); }
            catch (NpgsqlException error)
            {
                context.RequestServices.GetRequiredService<ILogger<SubscriptionRow>>()
                    .LogError(error, "Subscription storage is unavailable.");
                return Problem(503, "Subscriptions are unavailable", "subscriptions_unavailable");
            }
            catch (Exception error) when (error is InvalidDataException or InvalidCastException or OverflowException)
            {
                context.RequestServices.GetRequiredService<ILogger<SubscriptionRow>>()
                    .LogError(error, "Committed subscription data is inconsistent.");
                return Problem(500, "Committed subscription data is inconsistent", "subscriptions_inconsistent");
            }
        });
        group.MapGet("", List).Produces<SubscriptionPage>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Lists only the authenticated user's subscriptions within currently granted Viewer workflow types. " +
                "Optional exact scope and scopeId must be supplied together. limit=1..100 (default 50), descending UUID keyset cursor. " +
                "Owner and current authorization always constrain each page; destinations and signing references are never another user's data.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "scope", "scopeId", "limit", "cursor" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query,
                        Schema = name == "limit" ? new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100" } :
                            new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                return Task.CompletedTask;
            });
        group.MapPost("", Create).Accepts<SubscriptionRequest>("*/*")
            .Produces<SubscriptionRow>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Creates a user-owned durable completion/failure subscription after current scoped Viewer authorization. " +
                "scope=workflow|schedule|type. Completed includes every terminal run; Failed includes failure, timeout and abort. " +
                "Webhook uses an HTTPS destination (HTTP only on loopback), no URL credentials/query/fragment, and a signing secret reference; " +
                "Email uses one mailbox; Teams uses a configured workflow webhook secret reference, never its literal credential URL. " +
                "No resolved secret values are accepted or returned; duplicate configuration returns 409. Delivery is asynchronous.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // Keep unsupported media types in the bounded parser, not the router's bare 415 response.
                var body = operation.RequestBody?.Content?["*/*"]
                    ?? throw new InvalidOperationException("Subscription request schema is missing.");
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true, Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = body }
                };
                return Task.CompletedTask;
            });
        group.MapDelete("/{id}", Delete).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Deletes only the authenticated user's subscription and cascades its queued outbox rows. " +
                "Unsubscribe remains available after scope-role revocation. Unknown and other-user IDs share 404, including for global Admin.");
        return endpoints;
    }

    private static async Task<IResult> List(HttpContext context, NpgsqlDataSource source, IAuthorizationService authorization)
    {
        SubscriptionQuery filter;
        try { filter = SubscriptionQuery.Parse(context.Request.Query); }
        catch (ArgumentException error) { return Invalid(error.Message); }
        if (filter.Scope is { } scope)
        {
            var owner = await Resolve(source, scope, context.RequestAborted);
            if (owner is null || !await Allowed(context, authorization, owner.Type)) return Denied();
        }
        var (types, denied) = await RunQueryEndpoints.AuthorizeFilter(context, source, authorization,
            new RunQuery(null, null, null, null, null, null, null, new Dictionary<string, string>(), 50, null, null));
        if (denied is not null) return denied;
        await using var query = source.CreateCommand("""
            SELECT id,scope_kind,scope_id,events,channel,target,secret_ref,created_at FROM subscriptions
            WHERE principal_id=$1 AND type_scope=ANY($2)
                AND ($3::text IS NULL OR scope_kind=$3 AND scope_id=$4)
                AND ($5::uuid IS NULL OR id<$5)
            ORDER BY id DESC LIMIT $6
            """);
        query.Parameters.AddWithValue(Actor(context));
        query.Parameters.AddWithValue(types.ToArray());
        query.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Scope?.Kind ?? DBNull.Value);
        query.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Scope?.Id ?? DBNull.Value);
        query.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)filter.After ?? DBNull.Value);
        query.Parameters.AddWithValue(filter.Limit + 1);
        var rows = new List<SubscriptionRow>();
        await using (var reader = await query.ExecuteReaderAsync(context.RequestAborted))
            while (await reader.ReadAsync(context.RequestAborted)) rows.Add(Row(reader));
        var next = rows.Count > filter.Limit ? rows[filter.Limit - 1].Id.ToString("D") : null;
        return Results.Json(new SubscriptionPage(rows.Take(filter.Limit).ToArray(), next), SubscriptionJson.Default.SubscriptionPage);
    }

    private static async Task<IResult> Create(HttpContext context, NpgsqlDataSource source, IAuthorizationService authorization,
        TimeProvider time)
    {
        if (context.Request.Query.Count != 0) return Invalid("Subscription creation does not accept query parameters.");
        if (!context.Request.HasJsonContentType()) return Problem(415, "A JSON request content type is required", "unsupported_subscription_content");
        if (context.Request.ContentLength > MaximumRequestBytes) return TooLarge();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = MaximumRequestBytes;
        SubscriptionRequest request;
        try
        {
            request = (await context.Request.ReadFromJsonAsync(SubscriptionJson.Default.SubscriptionRequest, context.RequestAborted)
                ?? throw new ArgumentException("Supply a subscription object.")).Validate();
        }
        catch (JsonException) { return Invalid("Supply exactly the documented subscription properties and value types."); }
        catch (ArgumentException error) { return Invalid(error.Message); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
        var scope = SubscriptionScope.Parse(request.Scope, request.ScopeId);
        var owner = await Resolve(source, scope, context.RequestAborted);
        if (owner is null || !await Allowed(context, authorization, owner.Type)) return Denied();
        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        if (await Resolve(connection, scope, context.RequestAborted, locking: true) != owner) return Denied();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO subscriptions(id,principal_id,scope_kind,scope_id,type_scope,series_id,events,channel,target,secret_ref,created_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(Actor(context));
        insert.Parameters.AddWithValue(scope.Kind);
        insert.Parameters.AddWithValue(scope.Id);
        insert.Parameters.AddWithValue(owner.Type);
        insert.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)owner.Series ?? DBNull.Value);
        insert.Parameters.AddWithValue(request.Events);
        insert.Parameters.AddWithValue(request.Channel);
        insert.Parameters.AddWithValue(request.Target);
        insert.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)request.SecretRef ?? DBNull.Value);
        insert.Parameters.AddWithValue(now);
        try { await insert.ExecuteNonQueryAsync(context.RequestAborted); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation &&
            error.ConstraintName == "subscriptions_configuration")
        { return Problem(409, "This subscription configuration already exists", "duplicate_subscription"); }
        await Audit(connection, Actor(context), id, owner.Type, "subscription.create", scope, request.Channel, now, context.RequestAborted);
        await transaction.CommitAsync(context.RequestAborted);
        context.Response.Headers.Location = "/api/v1/subscriptions/" + id.ToString("D");
        return Results.Json(new SubscriptionRow(id, request.Scope, request.ScopeId, request.Events, request.Channel,
            request.Target, request.SecretRef, now), SubscriptionJson.Default.SubscriptionRow, statusCode: 201);
    }

    private static async Task<IResult> Delete(HttpContext context, string id, NpgsqlDataSource source, TimeProvider time)
    {
        if (context.Request.Query.Count != 0 || !SubscriptionScope.IsUuid(id)) return Invalid("Use a canonical nonempty subscription UUID, without query parameters.");
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        string type;
        SubscriptionScope scope;
        string channel;
        await using (var delete = new NpgsqlCommand("""
            DELETE FROM subscriptions WHERE id=$1 AND principal_id=$2 RETURNING type_scope,scope_kind,scope_id,channel
            """, connection))
        {
            delete.Parameters.AddWithValue(Guid.ParseExact(id, "D"));
            delete.Parameters.AddWithValue(Actor(context));
            await using var reader = await delete.ExecuteReaderAsync(context.RequestAborted);
            if (!await reader.ReadAsync(context.RequestAborted)) return Problem(404, "Subscription not found", "subscription_not_found");
            type = reader.GetString(0);
            scope = SubscriptionScope.Parse(reader.GetString(1), reader.GetString(2));
            channel = reader.GetString(3);
        }
        await Audit(connection, Actor(context), Guid.ParseExact(id, "D"), type, "subscription.delete", scope, channel,
            time.GetUtcNow(), context.RequestAborted);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.NoContent();
    }

    private sealed record Owner(string Type, Guid? Series);
    private static async Task<Owner?> Resolve(NpgsqlDataSource source, SubscriptionScope scope, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return await Resolve(connection, scope, token);
    }

    private static async Task<Owner?> Resolve(NpgsqlConnection connection, SubscriptionScope scope, CancellationToken token, bool locking = false)
    {
        var sql = scope.Kind switch
        {
            "type" => "SELECT id,NULL::uuid FROM workflow_types WHERE id=$1" + (locking ? " FOR KEY SHARE" : ""),
            "workflow" => "SELECT type_id,NULL::uuid FROM workflows WHERE id=$1" + (locking ? " FOR KEY SHARE" : ""),
            "schedule" => """
                SELECT w.type_id,r.id FROM schedules s JOIN run_series r ON r.schedule_id=s.id AND r.workflow_id=s.workflow_id
                JOIN workflows w ON w.id=s.workflow_id WHERE s.id=$1
                """ + (locking ? " FOR KEY SHARE OF s,r,w" : ""),
            _ => throw new InvalidDataException("Unknown committed subscription scope.")
        };
        await using var query = new NpgsqlCommand(sql, connection);
        if (scope.Kind == "schedule") query.Parameters.AddWithValue(Guid.ParseExact(scope.Id, "D"));
        else query.Parameters.AddWithValue(scope.Id);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var owner = new Owner(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
        if (await reader.ReadAsync(token)) throw new InvalidDataException("Ambiguous subscription owner.");
        return owner;
    }

    private static async Task<bool> Allowed(HttpContext context, IAuthorizationService authorization, string type) =>
        (await authorization.AuthorizeAsync(context.User, new TeamResource(TeamResourceKind.Type, type, context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded;

    private static async Task Audit(NpgsqlConnection connection, Guid actor, Guid id, string type, string action,
        SubscriptionScope scope, string channel, DateTimeOffset now, CancellationToken token)
    {
        await using var partition = new NpgsqlCommand("SELECT ensure_audit_partition($1)", connection);
        partition.Parameters.AddWithValue(DateOnly.FromDateTime(now.UtcDateTime));
        await partition.ExecuteNonQueryAsync(token);
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,team_id,summary)
            VALUES($1,$2,'User',$3,'Subscription',$4,$5,
                jsonb_build_object('scope',$6::text,'scopeId',$7::text,'channel',$8::text))
            """, connection);
        audit.Parameters.AddWithValue(now);
        audit.Parameters.AddWithValue(actor.ToString("D"));
        audit.Parameters.AddWithValue(action);
        audit.Parameters.AddWithValue(id.ToString("D"));
        audit.Parameters.AddWithValue(type.Split('/')[0]);
        audit.Parameters.AddWithValue(scope.Kind);
        audit.Parameters.AddWithValue(scope.Id);
        audit.Parameters.AddWithValue(channel);
        await audit.ExecuteNonQueryAsync(token);
    }

    private static SubscriptionRow Row(NpgsqlDataReader reader)
    {
        var row = new SubscriptionRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetFieldValue<string[]>(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7));
        try { new SubscriptionRequest(row.Scope, row.ScopeId, row.Events, row.Channel, row.Target, row.SecretRef).Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid committed subscription.", error); }
        return row;
    }
    private static Guid Actor(HttpContext context) => Guid.ParseExact(context.User.FindFirstValue("oid")!, "D");
    private static IResult Denied() => Results.Problem(TeamAuthorizationExtensions.Forbidden());
    private static IResult Invalid(string detail) => Results.Problem(statusCode: 400, title: "Invalid subscription query or request",
        detail: detail, extensions: new Dictionary<string, object?> { ["reason"] = "invalid_subscription" });
    private static IResult TooLarge() => Problem(413, "Subscription request exceeds its byte limit", "subscription_too_large");
    private static IResult Problem(int status, string title, string reason) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
