using System.Text.Json;
using Wf.Engine.Core.Timers;

namespace Wf.Engine.Core.Tests;

public sealed class RetryTimeoutTests
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    }

    [Theory]
    [InlineData(TimerScope.Run)]
    [InlineData(TimerScope.Node)]
    [InlineData(TimerScope.Attempt)]
    public void DeadlineProposesCancelThenKillAfterGraceWithoutPrematureMutation(TimerScope scope)
    {
        var clock = new Clock();
        var tracker = new DeadlineTracker(clock);
        var entry = new TimerEntry(new(scope, "work"), clock.UtcNow.AddSeconds(10));
        tracker.Track(entry);
        Assert.Empty(tracker.GetDue());
        clock.UtcNow += TimeSpan.FromSeconds(10);
        var cancel = Assert.Single(tracker.GetDue());
        Assert.Equal(TimerActionKind.Cancel, cancel.Kind);
        Assert.Equal(entry, Assert.Single(tracker.Snapshot));
        tracker.Apply(cancel);
        Assert.Throws<InvalidOperationException>(() => tracker.Apply(cancel));
        Assert.Empty(tracker.GetDue());
        clock.UtcNow += TimeSpan.FromSeconds(29);
        Assert.Empty(tracker.GetDue());
        clock.UtcNow += TimeSpan.FromSeconds(1);
        var kill = Assert.Single(tracker.GetDue());
        Assert.Equal(TimerActionKind.Kill, kill.Kind);
        tracker.Apply(kill);
        Assert.Empty(tracker.GetDue());
    }

    [Fact]
    public void RestoringCancelledAttemptPreservesOriginalKillTime()
    {
        var clock = new Clock();
        var tracker = new DeadlineTracker(clock);
        tracker.Track(new(new(TimerScope.Attempt, "node/task/1"), clock.UtcNow));
        tracker.Apply(Assert.Single(tracker.GetDue()));
        var json = JsonSerializer.Serialize(tracker.Snapshot);
        clock.UtcNow += TimeSpan.FromSeconds(31);
        var restored = new DeadlineTracker(clock, restored: JsonSerializer.Deserialize<TimerEntry[]>(json));
        Assert.Equal(TimerActionKind.Kill, Assert.Single(restored.GetDue()).Kind);
        Assert.Equal(tracker.Snapshot[0].KillAt, restored.Snapshot[0].KillAt);
    }

    [Fact]
    public void RetryTimeSurvivesFailoverAndDispatchIsNotRepeatedAfterCommit()
    {
        var clock = new Clock();
        var tracker = new DeadlineTracker(clock);
        tracker.Track(new(new(TimerScope.Retry, "node/task"), RetryAt: clock.UtcNow.AddMinutes(1)));
        var restored = new DeadlineTracker(clock, restored: tracker.Snapshot);
        clock.UtcNow += TimeSpan.FromMinutes(1);
        var dispatch = Assert.Single(restored.GetDue());
        Assert.Equal(TimerActionKind.DispatchRetry, dispatch.Kind);
        restored.Apply(dispatch);
        Assert.Empty(new DeadlineTracker(clock, restored: restored.Snapshot).GetDue());
    }

    [Fact]
    public void ExpiredDeadlineWinsOverRetryAtTheSameTime()
    {
        var clock = new Clock();
        var tracker = new DeadlineTracker(clock);
        tracker.Track(new(new(TimerScope.Retry, "node/task"), clock.UtcNow, clock.UtcNow));
        var cancel = Assert.Single(tracker.GetDue());
        Assert.Equal(TimerActionKind.Cancel, cancel.Kind);
        Assert.Null(cancel.After.RetryAt);
    }

    [Fact]
    public void CompletingAttemptDisarmsAllActionsAndStaleActionsAreRejected()
    {
        var clock = new Clock();
        var tracker = new DeadlineTracker(clock);
        var key = new TimerKey(TimerScope.Attempt, "node/task/1");
        tracker.Track(new(key, clock.UtcNow));
        var cancel = Assert.Single(tracker.GetDue());
        tracker.Complete(key);
        Assert.Empty(tracker.GetDue());
        Assert.Throws<InvalidOperationException>(() => tracker.Apply(cancel));
        Assert.Throws<KeyNotFoundException>(() => tracker.Complete(key));
    }

    [Fact]
    public void DuplicateOrMalformedRestoredTimersCannotSilentlyOverrideState()
    {
        var clock = new Clock();
        var entry = new TimerEntry(new(TimerScope.Run, "run"), clock.UtcNow);
        Assert.Throws<ArgumentException>(() => new DeadlineTracker(clock, restored: [entry, entry]));
        Assert.Throws<ArgumentException>(() => new DeadlineTracker(clock, restored: [entry with { DeadlineAt = null }]));
        Assert.Throws<ArgumentException>(() => new DeadlineTracker(clock, restored:
            [entry with { Phase = TimerPhase.CancelRequested, KillAt = null }]));
    }
}
