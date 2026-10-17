using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public sealed record ScheduleListQuery(string WorkflowId, int Limit, Guid? AfterId = null)
{
    public static ScheduleListQuery Parse(IQueryCollection query)
    {
        if (query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("workflowId" or "limit" or "cursor")))
            throw new ArgumentException("Use distinct workflowId, limit and cursor filters.");
        var common = RunQuery.Parse(new QueryCollection(query.Where(pair => pair.Key != "cursor")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        if (common.WorkflowId is not { } workflow || !WorkflowIdentity.TryGetTypeScope(workflow, out _))
            throw new ArgumentException("An exact workflowId is required.");
        var result = new ScheduleListQuery(workflow, common.Limit);
        if (!query.TryGetValue("cursor", out var values)) return result;
        var cursor = values[0]!;
        if (cursor.Length is < 100 or > 160) throw InvalidCursor();
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw InvalidCursor(); }
        var parts = decoded.Split('\n');
        if (parts.Length != 3 || parts[0] != "s1" || !Guid.TryParseExact(parts[1], "D", out var id) ||
            id == Guid.Empty || parts[1] != id.ToString("D") || parts[2] != result.Binding() ||
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded)) != cursor) throw InvalidCursor();
        return result with { AfterId = id };
    }

    private string Binding() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        WorkflowId + "\n" + Limit.ToString(CultureInfo.InvariantCulture))));
    public string Cursor(Guid last) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("s1\n" + last.ToString("D") + "\n" + Binding()));
    private static ArgumentException InvalidCursor() => new("Invalid schedule cursor; keep the original workflow and limit.");
}
