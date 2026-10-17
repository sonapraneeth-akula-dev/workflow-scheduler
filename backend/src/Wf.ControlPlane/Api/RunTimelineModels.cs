using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Api;

public sealed record RunTimelineResponse(Guid RunId, long LastSeq, string Status,
    IReadOnlyList<RunTimelineAttempt> Attempts)
{
    public static RunTimelineResponse From(RunSnapshot snapshot,
        IReadOnlyDictionary<(string TaskKey, int Attempt), string?> agents, IReadOnlyList<string>? taskOrder = null,
        IReadOnlyDictionary<(string TaskKey, int Attempt), DateTimeOffset?>? resumedFrom = null)
    {
        var metrics = RunMetricsResponse.From(snapshot);
        if (taskOrder is not null && (taskOrder.Count != metrics.Tasks.Count ||
            !taskOrder.ToHashSet(StringComparer.Ordinal).SetEquals(metrics.Tasks.Keys)))
            throw new InvalidDataException("Run manifest task identities are inconsistent.");
        var attempts = (taskOrder ?? metrics.Tasks.Keys.ToArray()).SelectMany(key =>
            metrics.Tasks[key].Attempts.Select(attempt => new RunTimelineAttempt(
                key[..key.IndexOf('/')], key, attempt.Attempt, attempt.QueuedAt, attempt.DispatchedAt,
                attempt.StartedAt, attempt.EndedAt, attempt.QueueWaitMs, attempt.DurationMs,
                attempt.TerminalStatus, attempt.FailureCategory, agents.GetValueOrDefault((key, attempt.Attempt)),
                resumedFrom?.GetValueOrDefault((key, attempt.Attempt))))).ToArray();
        return new(metrics.RunId, metrics.LastSeq, metrics.Status, attempts);
    }
}

public sealed record RunTimelineAttempt(string NodeKey, string TaskKey, int Attempt,
    DateTimeOffset QueuedAt, DateTimeOffset DispatchedAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    double QueueWaitMs, double DurationMs, string? TerminalStatus, string? FailureCategory, string? AgentId,
    DateTimeOffset? ResumedFrom);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RunTimelineResponse))]
public partial class RunTimelineJsonContext : JsonSerializerContext;
