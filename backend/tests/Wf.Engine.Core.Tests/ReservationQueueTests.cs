using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Tests;

public sealed class ReservationQueueTests
{
    [Theory]
    [InlineData(WhenUnavailable.Queue, 1, true, ReservationDisposition.Reserved)]
    [InlineData(WhenUnavailable.Reject, 1, true, ReservationDisposition.Reserved)]
    [InlineData(WhenUnavailable.Queue, 1, false, ReservationDisposition.Queued)]
    [InlineData(WhenUnavailable.Reject, 1, false, ReservationDisposition.Rejected)]
    [InlineData(WhenUnavailable.Queue, 2, true, ReservationDisposition.Queued)]
    [InlineData(WhenUnavailable.Reject, 2, true, ReservationDisposition.Rejected)]
    [InlineData(WhenUnavailable.Queue, 20, false, ReservationDisposition.Queued)]
    public void Only_available_head_can_allocate(WhenUnavailable policy, int position, bool available,
        ReservationDisposition expected) => Assert.Equal(expected, ReservationQueue.Decide(policy, position, available));

    [Fact]
    public void Invalid_policy_and_position_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReservationQueue.Decide((WhenUnavailable)9, 1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReservationQueue.Decide(WhenUnavailable.Queue, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeDefinition("work", [], [], whenUnavailable: (WhenUnavailable)9));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejection_ends_queued_or_running_run_with_durable_clear_reason_and_no_unfinished_entities(bool started)
    {
        var run = NewRun();
        var now = DateTimeOffset.UtcNow;
        if (started) run.Append(EventEntity.Run, null, (int)RunStatus.Running, now);
        var before = run.Snapshot;
        var changes = ReservationQueue.Reject(run, "work", now);
        Assert.Equal(before, run.Snapshot);
        foreach (var change in changes) run.Apply(change);
        Assert.Equal(RunStatus.Failed, run.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Failed, run.Snapshot.Nodes["work"]);
        Assert.All(run.Snapshot.Nodes.Values, value => Assert.True(TransitionTable.IsTerminal(value)));
        Assert.All(run.Snapshot.Tasks.Values, value => Assert.True(TransitionTable.IsTerminal(value)));
        Assert.Equal("ReservationUnavailable", run.Snapshot.FailureReason);
        Assert.Equal(FailureCategory.Placement, changes[^1].FailureCategory);
        var replay = RunState.Restore(before, changes, Assert.Fail).Snapshot;
        Assert.Equal(run.Snapshot.Status, replay.Status);
        Assert.Equal(run.Snapshot.FailureReason, replay.FailureReason);
        Assert.Equal(run.Snapshot.LastSequence, replay.LastSequence);
        Assert.Equal(run.Snapshot.Nodes.OrderBy(pair => pair.Key), replay.Nodes.OrderBy(pair => pair.Key));
        Assert.Equal(run.Snapshot.Tasks.OrderBy(pair => pair.Key), replay.Tasks.OrderBy(pair => pair.Key));
        Assert.Throws<InvalidOperationException>(() => ReservationQueue.Reject(run, "work", now));
    }

    [Fact]
    public void Rejection_preserves_already_completed_work()
    {
        var run = NewRun();
        var now = DateTimeOffset.UtcNow;
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, now);
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running, ExecutionStatus.Succeeded })
            run.Append(EventEntity.Task, "other/task", (int)state, now);
        foreach (var change in ReservationQueue.Reject(run, "work", now)) run.Apply(change);
        Assert.Equal(ExecutionStatus.Succeeded, run.Snapshot.Tasks["other/task"]);
    }

    private static RunState NewRun() => new(Guid.CreateVersion7(),
        [new("work", [], [new("task")]), new("other", [], [new("task")])],
        new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
}
