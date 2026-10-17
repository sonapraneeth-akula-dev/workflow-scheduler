using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public sealed record AuditRow(string Id, DateTimeOffset At, string Actor, string ActorKind, string Action,
    string TargetKind, string TargetId, string? TeamId, JsonElement Summary);
public sealed record AuditPage(AuditRow[] Items, string? NextCursor);
public sealed record AuditPosition(DateTimeOffset At, long Id);

public sealed record AuditQuery(string? Actor, string? TargetKind, string? TargetId, DateTimeOffset? From,
    DateTimeOffset? To, int Limit, AuditPosition? After = null)
{
    public static AuditQuery Parse(IQueryCollection query)
    {
        foreach (var pair in query)
            if (pair.Value.Count != 1 ||
                pair.Key is not ("actor" or "targetKind" or "targetId" or "from" or "to" or "limit" or "cursor"))
                throw new ArgumentException("Use distinct actor, targetKind, targetId, from, to, limit or cursor filters.");
        var common = RunQuery.Parse(new QueryCollection(query.Where(pair => pair.Key is "from" or "to" or "limit")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        var result = new AuditQuery(Text("actor"), Text("targetKind"), Text("targetId"), common.From, common.To, common.Limit);
        if (Value("cursor") is not { } cursor) return result;
        if (cursor.Length is < 100 or > 256) throw InvalidCursor();
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw InvalidCursor(); }
        var parts = decoded.Split('\n');
        if (parts.Length != 4 || parts[0] != "a1" ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks || ticks % 10 != 0 ||
            parts[1] != ticks.ToString(CultureInfo.InvariantCulture) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
            parts[2] != id.ToString(CultureInfo.InvariantCulture) || parts[3] != result.Binding() ||
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded)) != cursor)
            throw InvalidCursor();
        return result with { After = new(new(ticks, TimeSpan.Zero), id) };

        string? Value(string name) => query.TryGetValue(name, out var values) ? values[0] : null;
        string? Text(string name)
        {
            var value = Value(name);
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl)))
                throw new ArgumentException("Actor and target filters require 1-1024 characters without control characters.");
            return value;
        }
    }

    private string Binding()
    {
        var fields = new[] { Actor, TargetKind, TargetId, From?.ToString("O", CultureInfo.InvariantCulture),
            To?.ToString("O", CultureInfo.InvariantCulture), Limit.ToString(CultureInfo.InvariantCulture) };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields,
            AuditJsonContext.Default.StringArray)));
    }

    public string Cursor(AuditPosition position) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
        "a1\n" + position.At.UtcTicks.ToString(CultureInfo.InvariantCulture) + "\n" +
        position.Id.ToString(CultureInfo.InvariantCulture) + "\n" + Binding()));

    private static ArgumentException InvalidCursor() => new("Invalid audit cursor; keep the original filters and page limit.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AuditPage))]
[JsonSerializable(typeof(string[]))]
public partial class AuditJsonContext : JsonSerializerContext;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/audit", Read).RequireAuthorization(AuthExtensions.Admin)
            .WithMetadata(new ResponseCacheAttribute { NoStore = true })
            .Produces<AuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Global Admin audit trail. Exact actor/targetKind/targetId filters combine with AND. " +
                "from is inclusive and to exclusive (RFC3339 with offset, microsecond precision). " +
                "Newest first, stable timestamp/ID keyset bound to filters and limit (1-100, default 50). " +
                "IDs are decimal strings to preserve Int64 precision. Summaries are committed audit metadata, not workflow variables or storage capabilities.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "actor", "targetKind", "targetId", "from", "to", "limit", "cursor" })
                {
                    var schema = new OpenApiSchema { Type = name == "limit" ? JsonSchemaType.Integer : JsonSchemaType.String };
                    if (name is "from" or "to") schema.Format = "date-time";
                    if (name == "limit") { schema.Minimum = "1"; schema.Maximum = "100"; }
                    operation.Parameters.Add(new OpenApiParameter { Name = name, In = ParameterLocation.Query, Schema = schema });
                }
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, NpgsqlDataSource source)
    {
        context.Response.Headers.CacheControl = "no-store";
        AuditQuery filter;
        try { filter = AuditQuery.Parse(context.Request.Query); }
        catch (ArgumentException error) { return Results.Problem(statusCode: 400, title: "Invalid audit query", detail: error.Message); }
        try
        {
            await using var command = source.CreateCommand("""
                SELECT id,ts,actor,actor_kind,action,target_kind,target_id,team_id,summary::text
                FROM audit_log
                WHERE ($1::text IS NULL OR actor=$1) AND ($2::text IS NULL OR target_kind=$2)
                    AND ($3::text IS NULL OR target_id=$3) AND ($4::timestamptz IS NULL OR ts >= $4)
                    AND ($5::timestamptz IS NULL OR ts < $5)
                    AND ($6::timestamptz IS NULL OR (ts,id)<($6,$7::bigint))
                ORDER BY ts DESC,id DESC LIMIT $8
                """);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Actor ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.TargetKind ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.TargetId ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)filter.From ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)filter.To ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)filter.After?.At ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Bigint, (object?)filter.After?.Id ?? DBNull.Value);
            command.Parameters.AddWithValue(filter.Limit + 1);
            var rows = new List<(AuditRow Row, AuditPosition Position)>();
            await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
            while (await reader.ReadAsync(context.RequestAborted))
            {
                var id = reader.GetInt64(0);
                var at = reader.GetFieldValue<DateTimeOffset>(1);
                using var summary = JsonDocument.Parse(reader.GetString(8));
                if (id <= 0 || summary.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                rows.Add((new(id.ToString(CultureInfo.InvariantCulture), at, reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    summary.RootElement.Clone()), new(at, id)));
            }
            var next = rows.Count > filter.Limit ? filter.Cursor(rows[filter.Limit - 1].Position) : null;
            return Results.Json(new AuditPage(rows.Take(filter.Limit).Select(row => row.Row).ToArray(), next),
                AuditJsonContext.Default.AuditPage);
        }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Audit trail unavailable"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidCastException)
        { return Results.Problem(statusCode: 500, title: "Committed audit metadata is inconsistent"); }
    }
}
