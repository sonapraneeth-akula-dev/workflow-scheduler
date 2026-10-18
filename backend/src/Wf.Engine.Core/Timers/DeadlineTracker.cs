using System.Collections.Immutable;

namespace Wf.Engine.Core.Timers;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow(); }
public enum TimerScope { Run, Node, Attempt, Retry }
public enum TimerPhase { Waiting, CancelRequested, KillRequested, RetryDispatched }
public enum TimerActionKind { Cancel, Kill, DispatchRetry }
public sealed record TimerKey(TimerScope Scope, string Key);
public sealed record TimerEntry(TimerKey Key, DateTimeOffset? DeadlineAt = null, DateTimeOffset? RetryAt = null,
    DateTimeOffset? KillAt = null, TimerPhase Phase = TimerPhase.Waiting);
public sealed record TimerAction(TimerActionKind Kind, DateTimeOffset At, TimerEntry Before, TimerEntry After);

public sealed class DeadlineTracker
{
    private readonly IClock clock;
    private readonly TimeSpan grace;
    private ImmutableDictionary<TimerKey, TimerEntry> timers = ImmutableDictionary<TimerKey, TimerEntry>.Empty;
    public ImmutableArray<TimerEntry> Snapshot => timers.Values.OrderBy(t => t.Key.Scope)
        .ThenBy(t => t.Key.Key, StringComparer.Ordinal).ToImmutableArray();

    public DeadlineTracker(IClock clock, TimeSpan? grace = null, IEnumerable<TimerEntry>? restored = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        this.grace = grace ?? TimeSpan.FromSeconds(30);
        if (this.grace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(grace));
        foreach (var entry in restored ?? [])
            Track(entry);
    }

    public void Track(TimerEntry entry)
    {
        Validate(entry);
        if (timers.ContainsKey(entry.Key))
            throw new ArgumentException($"Timer '{entry.Key}' is already tracked.", nameof(entry));
        timers = timers.Add(entry.Key, entry);
    }

    public ImmutableArray<TimerAction> GetDue()
    {
        var now = clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("The execution clock must use UTC.");
        var actions = ImmutableArray.CreateBuilder<TimerAction>();
        foreach (var entry in Snapshot)
        {
            var action = Propose(entry, now);
            if (action is not null)
                actions.Add(action);
        }
        return actions.ToImmutable();
    }

    public void Apply(TimerAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(action.Before);
        ArgumentNullException.ThrowIfNull(action.After);
        if (action.At.Offset != TimeSpan.Zero || action.At > clock.UtcNow ||
            !timers.TryGetValue(action.Before.Key, out var current) || current != action.Before ||
            Propose(current, action.At) != action)
            throw new InvalidOperationException("Timer action is stale or invalid; no state was changed.");
        timers = timers.SetItem(current.Key, action.After);
    }

    public void Complete(TimerKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!timers.ContainsKey(key))
            throw new KeyNotFoundException($"Timer '{key}' is not tracked.");
        timers = timers.Remove(key);
    }

    private TimerAction? Propose(TimerEntry entry, DateTimeOffset now)
    {
        if (entry.Phase == TimerPhase.Waiting)
        {
            if (entry.DeadlineAt <= now)
                return new(TimerActionKind.Cancel, now, entry,
                    entry with { Phase = TimerPhase.CancelRequested, KillAt = now + grace, RetryAt = null });
            if (entry.RetryAt <= now)
                return new(TimerActionKind.DispatchRetry, now, entry, entry with { Phase = TimerPhase.RetryDispatched });
        }
        if (entry.Phase == TimerPhase.CancelRequested && entry.KillAt <= now)
            return new(TimerActionKind.Kill, now, entry, entry with { Phase = TimerPhase.KillRequested });
        return null;
    }

    private static void Validate(TimerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key.Key);
        if (!Enum.IsDefined(entry.Key.Scope) || !Enum.IsDefined(entry.Phase) ||
            (entry.DeadlineAt is null && entry.RetryAt is null && entry.KillAt is null) ||
            new[] { entry.DeadlineAt, entry.RetryAt, entry.KillAt }.Any(t => t?.Offset != TimeSpan.Zero && t is not null) ||
            (entry.Phase is TimerPhase.CancelRequested or TimerPhase.KillRequested && entry.KillAt is null) ||
            (entry.Phase is TimerPhase.Waiting or TimerPhase.RetryDispatched && entry.KillAt is not null) ||
            (entry.Phase == TimerPhase.RetryDispatched && entry.RetryAt is null))
            throw new ArgumentException("Timer has invalid scope, phase or UTC timestamps.", nameof(entry));
    }
}
