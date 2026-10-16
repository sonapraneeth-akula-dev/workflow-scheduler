using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public sealed record SeriesFireResponse(DateTimeOffset FireAt, string Outcome, Guid? RunId, string? Reason);
public sealed record SeriesFiresPage(Guid SeriesId, ImmutableArray<SeriesFireResponse> Items, string? NextCursor);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SeriesFiresPage))]
public partial class SeriesFiresJson : JsonSerializerContext;

public static class SeriesFiresEndpoints
{
    public static IEndpointRouteBuilder MapSeriesFiresEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/series/{id:guid}/fires", Get)
            .RequireTeamRole(RegistryRole.Viewer, TeamResourceKind.Series, "id")
            .Produces<SeriesFiresPage>().ProducesProblem(400).ProducesProblem(403);
        return endpoints;
    }

    private static async Task<IResult> Get(HttpContext context, Guid id, NpgsqlDataSource source,
        int limit = 50, string? cursor = null)
    {
        if (limit is < 1 or > 100)
            return Results.Problem(statusCode: 400, title: "Limit must be between 1 and 100.");
        DateTimeOffset? before = null;
        if (cursor is not null)
        {
            if (cursor.Length != 11)
                return InvalidCursor();
            byte[] bytes;
            try { bytes = WebEncoders.Base64UrlDecode(cursor); }
            catch (FormatException) { return InvalidCursor(); }
            if (bytes.Length != sizeof(long) || WebEncoders.Base64UrlEncode(bytes) != cursor)
                return InvalidCursor();
            var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
            if (ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks || ticks % 10 != 0)
                return InvalidCursor();
            before = new(ticks, TimeSpan.Zero);
        }
        await using var query = source.CreateCommand("""
            SELECT f.fire_at,f.outcome,f.run_id,f.reason
            FROM run_series s JOIN schedule_fires f ON f.schedule_id=s.schedule_id
            WHERE s.id=$1 AND ($2::timestamptz IS NULL OR f.fire_at<$2)
            ORDER BY f.fire_at DESC LIMIT $3
            """);
        query.Parameters.AddWithValue(id);
        query.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz, Value = (object?)before ?? DBNull.Value });
        query.Parameters.AddWithValue(limit + 1);
        var items = ImmutableArray.CreateBuilder<SeriesFireResponse>();
        await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
        while (await reader.ReadAsync(context.RequestAborted))
        {
            items.Add(new(reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt16(1) switch
            {
                0 => "Started", 1 => "Skipped", 2 => "Missed", 3 => "CaughtUp",
                _ => throw new InvalidDataException("Unknown persisted schedule fire outcome.")
            }, reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
            var bytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, items[^1].FireAt.UtcTicks);
            next = WebEncoders.Base64UrlEncode(bytes);
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new SeriesFiresPage(id, items.ToImmutable(), next), SeriesFiresJson.Default.SeriesFiresPage);
    }

    private static IResult InvalidCursor() => Results.Problem(statusCode: 400, title: "Invalid series fire cursor.");
}
