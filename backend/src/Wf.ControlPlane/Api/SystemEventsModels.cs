using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Events;
using Wf.Contracts.Progress;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Api;

public sealed record SystemEvent(string Seq, string At, int Entity, string? Key, int State, int? Attempt,
    Guid? AgentId, string? SelectionReason, Guid? PlannedAgentId, string? WaitingReason, string? FailureCategory,
    LiveProgress? Progress)
{
    public static SystemEvent From(RunEvent change)
    {
        var safe = LiveEvent.From(change);
        if (change.FailureCategory is { } category && !Enum.IsDefined(category))
            throw new InvalidDataException("Unknown committed failure category.");
        if (change.Attempt is <= 0 or > short.MaxValue || change.WaitingReason is not (null or "WaitingForMachine") ||
            change.AgentId is { } agent && (agent == Guid.Empty || change.Entity != EventEntity.Task || change.State != 2))
            throw new InvalidDataException("Invalid committed attempt or placement metadata.");
        if (change.Progress is { } progress) TaskProgressProtocol.Validate(progress);
        return new(change.Sequence.ToString(CultureInfo.InvariantCulture), safe.At, safe.Entity, safe.Key, safe.State,
            safe.Attempt, safe.AgentId, safe.SelectionReason, safe.PlannedAgentId, safe.WaitingReason,
            change.FailureCategory?.ToString(), safe.Progress);
    }
}

public sealed record SystemEventsPage(Guid RunId, string WorkflowId, Guid VersionId, string LastSeq,
    SystemEvent[] Events, string? NextCursor);

public sealed record SystemEventsQuery(string? NodeKey, string? TaskKey, int? Attempt, int Limit,
    long AfterSeq = 0, long? ThroughSeq = null)
{
    public static SystemEventsQuery Parse(Guid run, IQueryCollection query)
    {
        if (query.Any(pair => pair.Value.Count != 1 ||
            pair.Key is not ("node" or "task" or "attempt" or "limit" or "cursor")))
            throw new ArgumentException("Use distinct node, task, attempt, limit and cursor filters.");
        var node = Value("node");
        var task = Value("task");
        if (node is not null && InvalidKey(node) || task is not null &&
            (task.Split('/') is not [var taskNode, var taskName] || InvalidKey(taskNode) ||
                InvalidKey(taskName) || node is not null && taskNode != node))
            throw new ArgumentException("Use an exact node key and full node/task key.");
        var attempt = Value("attempt") is { } text ? checked((int)Number(text, short.MaxValue, positive: true)) : (int?)null;
        var limit = Value("limit") is { } size ? checked((int)Number(size, 200, positive: true)) : 100;
        var result = new SystemEventsQuery(node, task, attempt, limit);
        if (Value("cursor") is not { } cursor) return result;
        if (cursor.Length is < 95 or > 256) throw InvalidCursor();
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw InvalidCursor(); }
        var parts = decoded.Split('\n');
        if (parts.Length != 4 || parts[0] != "e1" || parts[3] != result.Binding(run) ||
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded)) != cursor)
            throw InvalidCursor();
        var after = Number(parts[1], long.MaxValue, positive: true);
        var through = Number(parts[2], long.MaxValue, positive: true);
        if (after >= through) throw InvalidCursor();
        return result with { AfterSeq = after, ThroughSeq = through };

        string? Value(string key) => query.TryGetValue(key, out var value) ? value[0] : null;
    }

    public string Cursor(Guid run, long last, long through) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
        "e1\n" + last.ToString(CultureInfo.InvariantCulture) + "\n" +
        through.ToString(CultureInfo.InvariantCulture) + "\n" + Binding(run)));

    private string Binding(Guid run) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        run.ToString("D") + "\n" + NodeKey + "\n" + TaskKey + "\n" +
        Attempt?.ToString(CultureInfo.InvariantCulture) + "\n" + Limit.ToString(CultureInfo.InvariantCulture))));

    private static long Number(string text, long maximum, bool positive)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < (positive ? 1 : 0) || value > maximum || text != value.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Use a canonical bounded decimal integer.");
        return value;
    }

    private static bool InvalidKey(string value) => string.IsNullOrWhiteSpace(value) || value.Contains('/') ||
        value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 512;
    private static ArgumentException InvalidCursor() => new("Invalid events cursor; keep the same run, filters and limit.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SystemEventsPage))]
public partial class SystemEventsJson : JsonSerializerContext;
