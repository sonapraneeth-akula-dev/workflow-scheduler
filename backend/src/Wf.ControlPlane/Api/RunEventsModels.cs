using System.Globalization;
using System.Text.Json.Serialization;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Api;

public sealed record RunEventsPage(Guid RunId, long LastSeq, LiveEvent[] Events);

public sealed record RunSnapshotResponse(Guid RunId, string WorkflowId, Guid VersionId, Guid? SeriesId,
    int Status, long LastSeq, IReadOnlyDictionary<string, int> Nodes, IReadOnlyDictionary<string, int> Tasks,
    LiveAbort? Abort, string? EndedAt, Guid? ParentRunId, IReadOnlyDictionary<string, Guid> ReusedNodes);

public static class RunEventsQuery
{
    public const int MaximumEvents = 5000;

    public static Guid RunId(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var run) || run == Guid.Empty ||
            id != run.ToString("D") || id[14] != '7')
            throw new ArgumentException("Use a canonical lowercase version 7 run UUID.");
        return run;
    }

    public static long AfterSequence(IQueryCollection query)
    {
        if (query.Count != 1 || !query.TryGetValue("afterSeq", out var values) ||
            !query.Keys.Contains("afterSeq", StringComparer.Ordinal) || values.Count != 1)
            throw new ArgumentException("Supply only one afterSeq query parameter.");
        var text = values[0];
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < 0 || text != value.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("afterSeq must be a canonical nonnegative Int64 decimal.");
        return value;
    }

    public static bool RequiresSnapshot(long afterSeq, long lastSeq)
    {
        if (afterSeq < 0 || lastSeq < afterSeq) throw new ArgumentOutOfRangeException(nameof(afterSeq));
        return lastSeq - afterSeq > MaximumEvents;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunEventsPage))]
[JsonSerializable(typeof(RunSnapshotResponse))]
public partial class RunEventsJsonContext : JsonSerializerContext;
