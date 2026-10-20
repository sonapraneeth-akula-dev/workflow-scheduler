using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Wf.ControlPlane.Api;

public sealed record ScheduleListQuery(RunQuery Filter, Guid? AfterId = null, string? CursorBinding = null)
{
    public string? WorkflowId => Filter.WorkflowId;
    public int Limit => Filter.Limit;

    public static ScheduleListQuery Parse(IQueryCollection query)
    {
        if (query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("team" or "type" or "workflowId" or "limit" or "cursor")))
            throw new ArgumentException("Use distinct team, type, workflowId, limit and cursor filters.");
        var common = RunQuery.Parse(new QueryCollection(query.Where(pair => pair.Key != "cursor")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        var result = new ScheduleListQuery(common);
        if (!query.TryGetValue("cursor", out var values)) return result;
        var cursor = values[0]!;
        if (cursor.Length is < 100 or > 160) throw InvalidCursor();
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw InvalidCursor(); }
        var parts = decoded.Split('\n');
        if (parts.Length != 3 || parts[0] != (result.WorkflowId is null ? "s2" : "s1") ||
            !Guid.TryParseExact(parts[1], "D", out var id) ||
            id == Guid.Empty || parts[1] != id.ToString("D") ||
            parts[2].Length != 64 || parts[2].Any(character => !char.IsAsciiHexDigitLower(character)) ||
            result.WorkflowId is not null && parts[2] != result.Binding([]) ||
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded)) != cursor) throw InvalidCursor();
        return result with { AfterId = id, CursorBinding = parts[2] };
    }

    public string Binding(IEnumerable<string> visibleTypes) => WorkflowId is null ? Filter.Binding(visibleTypes)
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            WorkflowId + "\n" + Limit.ToString(CultureInfo.InvariantCulture))));
    public string Cursor(Guid last, IEnumerable<string>? visibleTypes = null) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
            (WorkflowId is null ? "s2\n" : "s1\n") + last.ToString("D") + "\n" + Binding(visibleTypes ?? [])));
    private static ArgumentException InvalidCursor() => new("Invalid schedule cursor; keep the original filters, limit and authorized scopes.");
}
