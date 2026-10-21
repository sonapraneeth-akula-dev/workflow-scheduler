using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Definition;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Api;

public sealed record RunHistoryVariable(string Key, string Type, bool Redacted);
public sealed record RunHistoryFire(Guid ScheduleId, DateTimeOffset FireAt, string Outcome, string? Reason,
    Guid? ScheduleConfigVersionId);
public sealed record RunHistorySchedule(Guid SeriesId, Guid ScheduleId, string Name, bool Paused, bool Deleted);
public sealed record RunHistoryRow(string Kind, DateTimeOffset At, string WorkflowId, Guid? SeriesId, Guid? RunId,
    Guid? VersionId, string? Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, double? DurationMs,
    string? Trigger, RunMetadata? Metadata, LiveAbort? Abort, RunHistoryVariable[] Variables, RunHistoryFire? Fire)
{
    [JsonRequired]
    public string? Version { get; init; }
    public string? TriggeredByName { get; init; }
    public RunFailureSummary? FailureSummary { get; init; }
    [JsonRequired]
    public SignOffState? SignOff { get; init; }
}
public sealed record RunHistoryPage(RunHistoryRow[] Items, string? NextCursor)
{
    [JsonRequired]
    public Guid? CurrentScheduleId { get; init; }
    public RunHistorySchedule[] Schedules { get; init; } = [];
}
public sealed record RunHistoryPosition(DateTimeOffset At, Guid Id, int Kind);

public sealed record RunHistoryQuery(RunQuery Runs, string Sort, string? AbortedBy, string? Outcome,
    RunHistoryPosition? After, string? CursorBinding)
{
    public bool Ascending => Sort == "createdAtAsc";
    public Guid[]? SeriesIds { get; init; }
    public bool IncludeManual { get; init; } = true;

    public static RunHistoryQuery Parse(IQueryCollection query, string? workflow = null, string? series = null)
    {
        foreach (var pair in query)
            if (pair.Value.Count != 1) throw new ArgumentException("Each history filter must occur exactly once.");
        var runFilters = query.Where(pair => pair.Key is not ("sort" or "abortedBy" or "outcome" or "cursor" or "seriesIds" or "includeManual"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Scope("workflowId", workflow);
        Scope("seriesId", series);
        var runs = RunQuery.Parse(new QueryCollection(runFilters));
        if (runs.WorkflowId is null && runs.SeriesId is null)
            throw new ArgumentException("History requires an exact workflowId or seriesId.");
        var sort = Value("sort") ?? "createdAtDesc";
        if (sort is not ("createdAtAsc" or "createdAtDesc"))
            throw new ArgumentException("sort must be createdAtAsc or createdAtDesc.");
        var actor = Value("abortedBy");
        if (actor is not null && (!Guid.TryParseExact(actor, "D", out var actorId) ||
            actorId == Guid.Empty || actor != actorId.ToString("D")))
            throw new ArgumentException("abortedBy must be a canonical lowercase nonempty principal UUID.");
        var outcome = Value("outcome");
        if (outcome is not null && outcome is not ("Run" or "Skipped" or "Missed"))
            throw new ArgumentException("outcome must be Run, Skipped or Missed.");
        Guid[]? selected = null;
        if (Value("seriesIds") is { } selection)
        {
            if (runs.SeriesId is not null)
                throw new ArgumentException("Use seriesIds only with workflow history, not an explicit seriesId.");
            var values = selection == "none" ? [] : selection.Split(',');
            if (values.Length > 32 || values.Any(value => !Guid.TryParseExact(value, "D", out var id) ||
                id == Guid.Empty || value != id.ToString("D")) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new ArgumentException("seriesIds must be none or at most 32 distinct canonical lowercase nonempty UUIDs.");
            selected = values.Select(Guid.Parse).Order().ToArray();
        }
        var manual = Value("includeManual") ?? "true";
        if (manual is not ("true" or "false"))
            throw new ArgumentException("includeManual must be true or false.");
        RunHistoryPosition? after = null;
        string? binding = null;
        if (Value("cursor") is { } cursor)
        {
            if (cursor.Length is < 100 or > 256) throw InvalidCursor();
            string decoded;
            try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw InvalidCursor(); }
            var parts = decoded.Split('\n');
            if (parts.Length != 5 || parts[0] != "h1" ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks || ticks % 10 != 0 ||
                parts[1] != ticks.ToString(CultureInfo.InvariantCulture) ||
                !Guid.TryParseExact(parts[2], "D", out var id) || id == Guid.Empty || parts[2] != id.ToString("D") ||
                parts[3] is not ("0" or "1") || parts[4].Length != 64 ||
                parts[3] == "0" && (parts[2][14] != '7' || parts[2][19] is not ('8' or '9' or 'a' or 'b')) ||
                parts[4].Any(character => !char.IsAsciiHexDigitLower(character)) ||
                WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded)) != cursor)
                throw InvalidCursor();
            after = new(new(ticks, TimeSpan.Zero), id, parts[3] == "0" ? 0 : 1);
            binding = parts[4];
        }
        return new(runs, sort, actor, outcome, after, binding) { SeriesIds = selected, IncludeManual = manual == "true" };

        string? Value(string key) => query.TryGetValue(key, out var values) ? values[0] : null;
        void Scope(string key, string? value)
        {
            if (value is null) return;
            if (runFilters.ContainsKey(key)) throw new ArgumentException("Do not override a history route's resource filter.");
            runFilters.Add(key, value);
        }
    }

    public string Binding(IEnumerable<string> types) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Runs.Binding(types) + "\n" + Sort + "\n" + AbortedBy + "\n" + Outcome +
            "\n" + (SeriesIds is null ? "*" : string.Join(',', SeriesIds)) + "\n" + IncludeManual)));

    public string Cursor(RunHistoryPosition last, IEnumerable<string> types) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("h1\n" +
            last.At.UtcTicks.ToString(CultureInfo.InvariantCulture) + "\n" + last.Id.ToString("D") + "\n" +
            last.Kind.ToString(CultureInfo.InvariantCulture) + "\n" + Binding(types)));

    public static RunHistoryVariable[] Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new List<RunHistoryVariable>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var key = item.GetProperty("key").GetString();
            var type = item.GetProperty("type").GetString();
            if (string.IsNullOrWhiteSpace(key) || !Enum.TryParse<VariableType>(type, out var parsed) ||
                !Enum.IsDefined(parsed) || parsed.ToString() != type) throw new InvalidDataException();
            values.Add(new(key, type, parsed == VariableType.Secret));
        }
        return values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
    }

    private static ArgumentException InvalidCursor() => new("The history cursor is invalid.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunHistoryPage))]
public partial class RunHistoryJsonContext : JsonSerializerContext;
