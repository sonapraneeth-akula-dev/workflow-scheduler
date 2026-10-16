using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Metrics;

public sealed record AttemptMetrics(int Attempt, DateTimeOffset QueuedAt, DateTimeOffset DispatchedAt,
    DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null, double QueueWaitMs = 0,
    double DurationMs = 0, ExecutionStatus? TerminalStatus = null);

/// <summary>TerminalStatus uses the owning entity's RunStatus or ExecutionStatus values.
/// Task duration sums execution time only; node and run duration measures their running lifecycle.</summary>
public sealed record EntityMetrics(DateTimeOffset? QueuedAt = null, DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null, double QueueWaitMs = 0, double DurationMs = 0, int RetryCount = 0,
    int? TerminalStatus = null)
{
    public DateTimeOffset? WaitingSince { get; init; }
    public ImmutableArray<AttemptMetrics> Attempts { get; init; } = [];
}

public sealed record RunMetrics(EntityMetrics Run, ImmutableDictionary<string, EntityMetrics> Nodes,
    ImmutableDictionary<string, EntityMetrics> Tasks);

/// <summary>Immutable projection of committed transitions. No wall clock, I/O, retry decisions or proposal mutation.</summary>
public static class RunMetricsRecorder
{
    public static RunMetrics Initialize(RunSnapshot snapshot, DateTimeOffset? queuedAt = null) => new(
        new(queuedAt?.ToUniversalTime(), EndedAt: snapshot.EndedAt,
            TerminalStatus: TransitionTable.IsTerminal(snapshot.Status) ? (int)snapshot.Status : null),
        snapshot.Nodes.ToImmutableDictionary(p => p.Key, p => new EntityMetrics(
            TerminalStatus: TransitionTable.IsTerminal(p.Value) ? (int)p.Value : null), StringComparer.Ordinal),
        snapshot.Tasks.ToImmutableDictionary(p => p.Key, p => new EntityMetrics(
            TerminalStatus: TransitionTable.IsTerminal(p.Value) ? (int)p.Value : null), StringComparer.Ordinal));

    public static RunMetrics Record(RunMetrics metrics, RunEvent change)
    {
        if (change.RunQueuedAt is { } queuedAt)
            metrics = metrics with { Run = metrics.Run with { QueuedAt = queuedAt } };
        if (change.Entity == EventEntity.Run)
            return metrics with { Run = Lifecycle(metrics.Run, change, change.State == (int)RunStatus.Running,
                TransitionTable.IsTerminal((RunStatus)change.State)) };
        if (change.Entity == EventEntity.Node)
            return metrics with { Nodes = metrics.Nodes.SetItem(change.Key!,
                Lifecycle(metrics.Nodes[change.Key!], change, change.State == (int)ExecutionStatus.Running,
                    TransitionTable.IsTerminal((ExecutionStatus)change.State))) };

        var key = change.Key!;
        var previous = metrics.Tasks[key];
        var task = Task(previous, change);
        var retries = task.RetryCount - previous.RetryCount;
        var node = key[..key.IndexOf('/')];
        return metrics with
        {
            Tasks = metrics.Tasks.SetItem(key, task),
            Nodes = metrics.Nodes.SetItem(node, metrics.Nodes[node] with
                { RetryCount = metrics.Nodes[node].RetryCount + retries }),
            Run = metrics.Run with { RetryCount = metrics.Run.RetryCount + retries }
        };
    }

    private static EntityMetrics Lifecycle(EntityMetrics metric, RunEvent change, bool started, bool terminal)
    {
        var queuedAt = metric.QueuedAt ?? change.At;
        var start = metric.StartedAt ?? (started ? change.At : (DateTimeOffset?)null);
        return metric with
        {
            QueuedAt = queuedAt, StartedAt = start,
            EndedAt = terminal ? change.At : metric.EndedAt,
            QueueWaitMs = Elapsed(queuedAt, start ?? change.At),
            DurationMs = terminal ? Elapsed(start, change.At) : metric.DurationMs,
            TerminalStatus = terminal ? change.State : metric.TerminalStatus
        };
    }

    private static EntityMetrics Task(EntityMetrics metric, RunEvent change)
    {
        var status = (ExecutionStatus)change.State;
        if (status == ExecutionStatus.Ready)
            return metric with { QueuedAt = change.At, WaitingSince = change.At };
        if (status == ExecutionStatus.Dispatched)
        {
            var queuedAt = metric.WaitingSince ?? change.At;
            var number = change.Attempt ?? (metric.Attempts.Length == 0 ? 1 : metric.Attempts[^1].Attempt + 1);
            var attempt = new AttemptMetrics(number, queuedAt, change.At,
                QueueWaitMs: Elapsed(queuedAt, change.At));
            return metric with
            {
                Attempts = metric.Attempts.Add(attempt),
                RetryCount = metric.Attempts.Length,
                QueueWaitMs = metric.QueueWaitMs + attempt.QueueWaitMs
            };
        }
        var attempts = metric.Attempts;
        if (attempts.Length != 0 && attempts[^1].EndedAt is null)
        {
            var attempt = attempts[^1];
            var endedAt = change.AttemptEndedAt ?? change.At;
            if (status == ExecutionStatus.Running)
                attempt = attempt with { StartedAt = change.At, QueueWaitMs = Elapsed(attempt.QueuedAt, change.At) };
            else if (status == ExecutionStatus.Retrying || TransitionTable.IsTerminal(status))
                attempt = attempt with
                {
                    EndedAt = endedAt,
                    QueueWaitMs = Elapsed(attempt.QueuedAt, attempt.StartedAt ?? endedAt),
                    DurationMs = Elapsed(attempt.StartedAt, endedAt),
                    TerminalStatus = change.AttemptStatus ?? (status == ExecutionStatus.Retrying ? ExecutionStatus.Failed : status)
                };
            attempts = attempts.SetItem(attempts.Length - 1, attempt);
        }
        var terminal = TransitionTable.IsTerminal(status);
        return metric with
        {
            StartedAt = metric.StartedAt ?? (status == ExecutionStatus.Running ? change.At : (DateTimeOffset?)null),
            EndedAt = terminal ? change.At : null,
            TerminalStatus = terminal ? change.State : null,
            WaitingSince = status == ExecutionStatus.Retrying ? change.AttemptEndedAt ?? change.At : metric.WaitingSince,
            Attempts = attempts,
            QueueWaitMs = attempts.Length == 0 && terminal ? Elapsed(metric.QueuedAt, change.At) : attempts.Sum(a => a.QueueWaitMs),
            DurationMs = attempts.Sum(a => a.DurationMs)
        };
    }

    private static double Elapsed(DateTimeOffset? start, DateTimeOffset end) =>
        start is { } at ? Math.Max(0, (end - at).TotalMilliseconds) : 0;
}
