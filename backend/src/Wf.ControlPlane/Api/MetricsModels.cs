using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core;
using Wf.Engine.Core.Metrics;
using Wf.Engine.Core.StateMachine;

namespace Wf.ControlPlane.Api;

public sealed record RunMetricsResponse(Guid RunId, long LastSeq, string Status, MetricEntity Run,
    IReadOnlyDictionary<string, MetricEntity> Stages, IReadOnlyDictionary<string, MetricEntity> Nodes,
    IReadOnlyDictionary<string, MetricEntity> Tasks)
{
    public static RunMetricsResponse From(RunSnapshot snapshot)
    {
        var metrics = snapshot.Metrics ?? throw new InvalidDataException("Run metrics are missing.");
        if (metrics.Nodes is null || metrics.Tasks is null || metrics.Run is null ||
            !metrics.Nodes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.Nodes.Keys) ||
            !metrics.Tasks.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.Tasks.Keys))
            throw new InvalidDataException("Run metrics do not match the committed graph.");
        var nodes = snapshot.Nodes.ToDictionary(p => p.Key,
            p => Entity(metrics.Nodes[p.Key], p.Value.ToString(), (int)p.Value, TransitionTable.IsTerminal(p.Value), false),
            StringComparer.Ordinal);
        var tasks = snapshot.Tasks.ToDictionary(p => p.Key,
            p => Entity(metrics.Tasks[p.Key], p.Value.ToString(), (int)p.Value, TransitionTable.IsTerminal(p.Value), true),
            StringComparer.Ordinal);
        var retries = tasks.GroupBy(t => t.Key[..t.Key.IndexOf('/')])
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Value.RetryCount));
        if (metrics.Run.RetryCount != tasks.Values.Sum(t => t.RetryCount) ||
            nodes.Any(n => n.Value.RetryCount != retries.GetValueOrDefault(n.Key)))
            throw new InvalidDataException("Run retry totals are inconsistent.");
        return new(snapshot.RunId, snapshot.LastSequence, snapshot.Status.ToString(),
            Entity(metrics.Run, snapshot.Status.ToString(), (int)snapshot.Status, TransitionTable.IsTerminal(snapshot.Status), false),
            nodes.Where(p => LifecycleStages.IsStage(p.Key)).ToDictionary(),
            nodes.Where(p => !LifecycleStages.IsStage(p.Key)).ToDictionary(), tasks);
    }

    private static MetricEntity Entity(EntityMetrics metric, string status, int state, bool terminal, bool task)
    {
        if (metric is null || metric.Attempts.IsDefault ||
            !Valid(metric.QueueWaitMs) || !Valid(metric.DurationMs) || metric.RetryCount < 0 ||
            !Utc(metric.QueuedAt) || !Utc(metric.StartedAt) || !Utc(metric.EndedAt) ||
            terminal != (metric.TerminalStatus is not null) ||
            metric.TerminalStatus is { } outcome && outcome != state ||
            metric.FailureCategory is { } category && (!Enum.IsDefined(category) ||
                status is not ("Retrying" or "Aborting" or "Failed" or "SoftFailed" or "TimedOut" or "Cancelled" or "Aborted")) ||
            !task && (!metric.Attempts.IsEmpty ||
                metric.DurationMs != (metric.EndedAt is { } end ? Elapsed(metric.StartedAt, end) : 0)))
            throw new InvalidDataException("Run entity metrics are inconsistent.");
        var attempts = metric.Attempts.Select(Attempt).ToArray();
        if (attempts.Zip(attempts.Skip(1)).Any(p => p.First.Attempt >= p.Second.Attempt || p.First.EndedAt is null) ||
            task && (metric.RetryCount != Math.Max(0, attempts.Length - 1) ||
                metric.DurationMs != attempts.Sum(a => a.DurationMs) ||
                attempts.Length > 0 && metric.QueueWaitMs != attempts.Sum(a => a.QueueWaitMs)))
            throw new InvalidDataException("Run attempt totals are inconsistent.");
        return new(status, metric.QueuedAt, metric.StartedAt, metric.EndedAt, metric.QueueWaitMs,
            metric.DurationMs, metric.RetryCount, terminal ? status : null, metric.FailureCategory?.ToString(), attempts);
    }

    private static MetricAttempt Attempt(AttemptMetrics metric)
    {
        if (metric is null || metric.Attempt is <= 0 or > short.MaxValue ||
            !Utc(metric.QueuedAt) || !Utc(metric.DispatchedAt) || !Utc(metric.StartedAt) || !Utc(metric.EndedAt) ||
            !Valid(metric.QueueWaitMs) || !Valid(metric.DurationMs) ||
            (metric.EndedAt is null) != (metric.TerminalStatus is null) ||
            metric.TerminalStatus is { } status && (!Enum.IsDefined(status) || !TransitionTable.IsTerminal(status)) ||
            metric.FailureCategory is { } category && (!Enum.IsDefined(category) ||
                metric.TerminalStatus is not { } failure || !Failed(failure)) ||
            metric.QueueWaitMs != Elapsed(metric.QueuedAt, metric.StartedAt ?? metric.EndedAt ?? metric.DispatchedAt) ||
            metric.DurationMs != (metric.EndedAt is { } end ? Elapsed(metric.StartedAt, end) : 0))
            throw new InvalidDataException("Run attempt metrics are inconsistent.");
        return new(metric.Attempt, metric.QueuedAt, metric.DispatchedAt, metric.StartedAt, metric.EndedAt,
            metric.QueueWaitMs, metric.DurationMs, metric.TerminalStatus?.ToString(), metric.FailureCategory?.ToString());
    }

    private static bool Failed(ExecutionStatus status) => status is ExecutionStatus.Failed or ExecutionStatus.SoftFailed or
        ExecutionStatus.TimedOut or ExecutionStatus.Cancelled;
    private static double Elapsed(DateTimeOffset? start, DateTimeOffset end) =>
        start is { } at ? Math.Max(0, (end - at).TotalMilliseconds) : 0;
    private static bool Valid(double value) => double.IsFinite(value) && value >= 0;
    private static bool Utc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero;
}

public sealed record MetricEntity(string Status, DateTimeOffset? QueuedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt, double QueueWaitMs, double DurationMs, int RetryCount, string? TerminalStatus,
    string? FailureCategory, IReadOnlyList<MetricAttempt> Attempts);

public sealed record MetricAttempt(int Attempt, DateTimeOffset QueuedAt, DateTimeOffset DispatchedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, double QueueWaitMs, double DurationMs,
    string? TerminalStatus, string? FailureCategory);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RunMetricsResponse))]
public partial class MetricsJsonContext : JsonSerializerContext;
