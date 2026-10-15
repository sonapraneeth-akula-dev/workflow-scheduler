using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Runs;

namespace Wf.ControlPlane.Api;

public sealed class IdempotencyFilter(NpgsqlDataSource source, TimeProvider time) : IEndpointFilter
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var headers)) return await next(invocation);
        if (headers.Count != 1 || headers[0] is not { Length: > 0 and <= 200 } key ||
            key.Any(character => character is < '!' or > '~'))
            return Results.Problem(statusCode: 400, title: "Invalid Idempotency-Key",
                detail: "Supply one key of 1 to 200 visible ASCII characters without whitespace.");
        var authorized = context.Features.Get<AuthorizedRunRequest>()
            ?? throw new InvalidOperationException("Idempotency requires an authorized run request.");
        var route = context.Request.Method + " " +
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var fingerprint = Fingerprint(authorized.IdempotencyRequest ??
            JsonSerializer.SerializeToElement(authorized.Request, RunJsonContext.Default.TriggerRunRequest));
        var now = time.GetUtcNow();
        var token = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO idempotency_keys(principal_id,route,key,request_hash,created_at,expires_at)
            VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(principal_id,route,key) DO NOTHING
            """, connection, transaction))
        {
            AddIdentity(insert, authorized.Actor, route, key);
            insert.Parameters.AddWithValue(fingerprint);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(now.Add(Retention));
            await insert.ExecuteNonQueryAsync(token);
        }
        bool expired;
        byte[] storedHash;
        string? response;
        await using (var read = new NpgsqlCommand("""
            SELECT request_hash,response::text,expires_at FROM idempotency_keys
            WHERE principal_id=$1 AND route=$2 AND key=$3 FOR UPDATE
            """, connection, transaction))
        {
            AddIdentity(read, authorized.Actor, route, key);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("An idempotency reservation is missing.");
            storedHash = reader.GetFieldValue<byte[]>(0);
            response = reader.IsDBNull(1) ? null : reader.GetString(1);
            expired = reader.GetFieldValue<DateTimeOffset>(2) <= now;
        }
        if (!expired)
        {
            if (!CryptographicOperations.FixedTimeEquals(fingerprint, storedHash))
                return Results.Problem(statusCode: 409, title: "Idempotency-Key conflict",
                    detail: "This key already identifies a different request. Use a new key for a new submission.");
            if (response is not null)
            {
                var original = JsonSerializer.Deserialize(response, RunJsonContext.Default.TriggerRunResponse)
                    ?? throw new InvalidDataException("The idempotency response is null.");
                if (original.RunId == Guid.Empty) throw new InvalidDataException("The idempotency response has no run identity.");
                await transaction.CommitAsync(token);
                return Results.Json(original, RunJsonContext.Default.TriggerRunResponse, statusCode: 202);
            }
        }
        context.Features.Set(transaction);
        object? result;
        try { result = await next(invocation); }
        finally { context.Features.Set<NpgsqlTransaction>(null); }
        if (result is not IStatusCodeHttpResult { StatusCode: 202 } ||
            result is not IValueHttpResult { Value: TriggerRunResponse accepted })
            return result;
        var json = JsonSerializer.Serialize(accepted, RunJsonContext.Default.TriggerRunResponse);
        await using (var update = new NpgsqlCommand("""
            UPDATE idempotency_keys SET request_hash=$4,status_code=202,response=$5,
                created_at=$6,expires_at=$7 WHERE principal_id=$1 AND route=$2 AND key=$3
            """, connection, transaction))
        {
            AddIdentity(update, authorized.Actor, route, key);
            update.Parameters.AddWithValue(fingerprint);
            update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json);
            update.Parameters.AddWithValue(now);
            update.Parameters.AddWithValue(now.Add(Retention));
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidDataException("The idempotency response was not persisted.");
        }
        await transaction.CommitAsync(token);
        return result;
    }

    private static void AddIdentity(NpgsqlCommand command, Guid actor, string route, string key)
    {
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(route);
        command.Parameters.AddWithValue(key);
    }

    private static byte[] Fingerprint(JsonElement request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(request, writer);
        return SHA256.HashData(buffer.WrittenSpan);

        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(item, writer);
                writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }
}
