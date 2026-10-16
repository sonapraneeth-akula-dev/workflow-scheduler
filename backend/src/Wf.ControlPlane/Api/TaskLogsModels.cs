using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;

namespace Wf.ControlPlane.Api;

public sealed record TaskLogRecord(long Line, LogFrame Frame);
public sealed record TaskLogMatch(long Line, int Index, int Length, LogFrame Frame);
public sealed record TaskLogsPage(long TotalLines, long DroppedLines, bool Truncated, bool CaptureInterrupted,
    string? TruncationMarker, IReadOnlyList<TaskLogRecord> Records, IReadOnlyList<TaskLogMatch> Matches, string? NextCursor);

public sealed record TaskLogsQuery(long Offset, int Length, string? Search, bool Download, long AfterLine, int AfterIndex)
{
    public static TaskLogsQuery Parse(IQueryCollection query)
    {
        foreach (var pair in query)
            if (pair.Key is not ("offset" or "length" or "search" or "download" or "cursor") || pair.Value.Count != 1)
                throw new ArgumentException("Use each supported log query parameter only once.");
        var offset = Number("offset", 0, long.MaxValue - 1000);
        var length = (int)Number("length", 200, 1000, 1);
        var search = query.ContainsKey("search") ? query["search"].ToString() : null;
        var download = query.ContainsKey("download");
        if (download && query["download"] != "true" ||
            search is not null && (search.Length is < 1 or > 2048 || download) ||
            (search is not null || download) && (query.ContainsKey("offset") || query.ContainsKey("length")) ||
            download && query.ContainsKey("cursor") || search is null && query.ContainsKey("cursor"))
            throw new ArgumentException("Use offset/length, search/cursor, or download=true, not a mixture.");
        long line = 1;
        var index = 0;
        if (query.ContainsKey("cursor"))
        {
            var cursor = query["cursor"].ToString();
            try
            {
                if (cursor.Length is < 1 or > 64) throw new FormatException();
                var text = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor));
                var parts = text.Split(':');
                if (parts.Length != 2 ||
                    !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out line) || line < 1 ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index is < 0 or > 65536 ||
                    Cursor(line, index) != cursor) throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException)
            { throw new ArgumentException("The log search cursor is invalid."); }
        }
        return new(offset, length, search, download, line, index);

        long Number(string name, long fallback, long maximum, long minimum = 0)
        {
            if (!query.ContainsKey(name)) return fallback;
            var text = query[name].ToString();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < minimum || value > maximum || text != value.ToString(CultureInfo.InvariantCulture))
                throw new ArgumentException($"The log {name} is outside its canonical integer bounds.");
            return value;
        }
    }

    public static string Cursor(long line, int index) => WebEncoders.Base64UrlEncode(
        Encoding.UTF8.GetBytes(line.ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture)));

    public static AttemptRef Identity(string id, string task, string attempt)
    {
        // Kestrel deliberately leaves escaped slashes in a route segment. Decode exactly once.
        var key = Uri.UnescapeDataString(task);
        var parts = key.Split('/');
        if (!Guid.TryParseExact(id, "D", out var run) || run == Guid.Empty || id != run.ToString("D") || id[14] != '7' ||
            !uint.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number is 0 or > 32767 || attempt != number.ToString(CultureInfo.InvariantCulture) ||
            parts.Length != 2 || parts.Any(part => !CatalogQuery.ValidSegment(part) ||
                Encoding.UTF8.GetByteCount(part) > 512))
            throw new ArgumentException("Use a canonical run UUID, URI-escaped node/task key, and attempt from 1 to 32767.");
        return new() { RunId = id, NodeKey = parts[0], TaskKey = parts[1], Attempt = number };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskLogsPage))]
[JsonSerializable(typeof(LogFrame))]
public partial class TaskLogsJsonContext : JsonSerializerContext;
