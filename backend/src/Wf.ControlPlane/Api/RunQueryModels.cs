using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public sealed record RunQueryRow(Guid Id, string WorkflowId, Guid VersionId, Guid? SeriesId,
    string Team, string Type, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
public sealed record RunQueryPage(IReadOnlyList<RunQueryRow> Items, string? NextCursor);

public sealed record RunQuery(string? Team, string? Type, string? WorkflowId, Guid? SeriesId, RunStatus? Status,
    DateTimeOffset? From, DateTimeOffset? To, IReadOnlyDictionary<string, string> Metadata, int Limit,
    Guid? AfterId, string? CursorBinding)
{
    public static RunQuery Parse(IQueryCollection query)
    {
        if (query.Count > 41) throw new ArgumentException("Use at most 32 metadata filters and the documented run filters.");
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in query)
        {
            if (entry.Value.Count != 1)
                throw new ArgumentException("Each run filter must occur exactly once.");
            if (entry.Key.StartsWith("meta.", StringComparison.Ordinal))
            {
                var key = entry.Key[5..];
                if (!Text(key, 128) || !Text(entry.Value[0], 1024, allowEmpty: true) || metadata.Count == 32)
                    throw new ArgumentException("Metadata filters require a key of 1–128 characters and a string value of at most 1024 characters.");
                metadata.Add(key, entry.Value[0]!);
            }
            else if (entry.Key is not ("team" or "type" or "workflowId" or "seriesId" or "status" or "from" or "to" or "limit" or "cursor"))
                throw new ArgumentException("Unknown run filter. Use team, type, workflowId, seriesId, status, meta.<key>, from, to, limit or cursor.");
        }
        var team = Value("team");
        var type = Value("type");
        if (team is not null && !CatalogQuery.ValidSegment(team) ||
            type is not null && (team is null || !CatalogQuery.ValidSegment(type)))
            throw new ArgumentException("Use exact registered team/type names. A type filter requires its team.");
        var workflow = Value("workflowId");
        if (workflow is not null && (!WorkflowIdentity.TryGetTypeScope(workflow, out _) ||
            team is not null && workflow.Split('/')[0] != team || type is not null && workflow.Split('/')[1] != type))
            throw new ArgumentException("workflowId must be an exact team/type/workflow identity consistent with team and type.");
        Guid? series = Value("seriesId") is { } seriesText ? Uuid(seriesText, version7: false) : null;
        RunStatus? status = null;
        if (Value("status") is { } statusText)
        {
            if (!Enum.TryParse<RunStatus>(statusText, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != statusText)
                throw new ArgumentException("status must be one exact RunStatus name: Queued, Running, Aborting, Succeeded, Failed, Aborted or TimedOut.");
            status = parsed;
        }
        var from = Time(Value("from"));
        var to = Time(Value("to"));
        if (from is not null && to is not null && from >= to)
            throw new ArgumentException("from must precede to; created_at uses an inclusive from and exclusive to.");
        var limit = 50;
        if (Value("limit") is { } limitText &&
            (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 100 || limitText != limit.ToString(CultureInfo.InvariantCulture)))
            throw new ArgumentException("limit must be a canonical whole number from 1 to 100.");
        Guid? after = null;
        string? binding = null;
        if (Value("cursor") is { } cursor)
        {
            if (cursor.Length != 138) throw new ArgumentException("The run cursor is invalid.");
            string decoded;
            try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException)
            { throw new ArgumentException("The run cursor is invalid."); }
            var parts = decoded.Split('\n');
            if (parts.Length != 3 || parts[0] != "1" || parts[2].Length != 64 ||
                parts[2].Any(character => !char.IsAsciiHexDigitLower(character)) ||
                WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded)) != cursor)
                throw new ArgumentException("The run cursor is invalid.");
            after = Uuid(parts[1], version7: true);
            binding = parts[2];
        }
        return new(team, type, workflow, series, status, from, to, metadata, limit, after, binding);

        string? Value(string key) => query.TryGetValue(key, out var values) ? values[0] : null;
    }

    public string Binding(IEnumerable<string> visibleTypes)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            foreach (var text in new[] { Team, Type, WorkflowId, SeriesId?.ToString("D"), Status?.ToString(),
                From?.ToString("O", CultureInfo.InvariantCulture), To?.ToString("O", CultureInfo.InvariantCulture) })
                writer.WriteStringValue(text);
            writer.WriteNumberValue(Limit);
            writer.WriteStartObject();
            foreach (var pair in Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)) writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteStartArray();
            foreach (var scope in visibleTypes.Order(StringComparer.Ordinal)) writer.WriteStringValue(scope);
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    public string Cursor(Guid lastId, IEnumerable<string> visibleTypes) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("1\n" + lastId.ToString("D") + "\n" + Binding(visibleTypes)));

    private static Guid Uuid(string text, bool version7)
    {
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty || text != value.ToString("D") ||
            version7 && (text[14] != '7' || text[19] is not ('8' or '9' or 'a' or 'b')))
            throw new ArgumentException("Use a canonical lowercase nonempty UUID" + (version7 ? " version 7." : "."));
        return value;
    }

    private static DateTimeOffset? Time(string? text)
    {
        if (text is null) return null;
        if (text.Length > 40 || !(text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-') ||
            !DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) || value.Ticks % 10 != 0)
            throw new ArgumentException("from and to must be RFC3339 timestamps with an explicit offset and at most microsecond precision.");
        return value.ToUniversalTime();
    }

    private static bool Text(string? value, int maximum, bool allowEmpty = false) => value is not null &&
        (allowEmpty || !string.IsNullOrWhiteSpace(value)) && value.EnumerateRunes().Take(maximum + 1).Count() <= maximum &&
        !value.Any(char.IsControl);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunQueryPage))]
public partial class RunQueryJsonContext : JsonSerializerContext;
