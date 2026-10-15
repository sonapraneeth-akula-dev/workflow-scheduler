using System.Text.Json;
using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core;

namespace Wf.Engine.Core.Tests;

public sealed class RunStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LiveStateEqualsReplayAndSnapshotPlusTail()
    {
        var live = Create();
        var initial = live.Snapshot;
        live.Append(EventEntity.Run, null, (int)RunStatus.Running, Now);
        live.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, Now);
        var checkpoint = live.Snapshot;
        live.Append(EventEntity.Node, "node", (int)ExecutionStatus.Dispatched, Now);
        live.Append(EventEntity.Node, "node", (int)ExecutionStatus.Running, Now);
        live.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, Now);
        live.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, Now);
        live.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Running, Now);
        live.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Succeeded, Now);
        live.Append(EventEntity.Node, "node", (int)ExecutionStatus.Succeeded, Now);
        live.Append(EventEntity.Run, null, (int)RunStatus.Succeeded, Now);
        var replay = RunState.Restore(initial, live.Events, Assert.Fail);
        var restored = RunState.Restore(checkpoint, live.Events.Where(e => e.Sequence > checkpoint.LastSequence), Assert.Fail);
        Assert.Equal(JsonSerializer.Serialize(live.Snapshot), JsonSerializer.Serialize(replay.Snapshot));
        Assert.Equal(JsonSerializer.Serialize(live.Snapshot), JsonSerializer.Serialize(restored.Snapshot));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), live.Events.Select(e => e.Sequence));
        Assert.Equal(ExecutionStatus.Pending, initial.Nodes["node"]);
    }

    [Fact]
    public void CreatingEventDoesNotMutateStateBeforePersistenceAcknowledgesIt()
    {
        var run = Create();
        var proposed = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, Now);
        Assert.Equal(0, run.Snapshot.LastSequence);
        Assert.Equal(RunStatus.Queued, run.Snapshot.Status);
        Assert.Empty(run.Events);
        run.Apply(proposed);
        Assert.Equal(1, run.Snapshot.LastSequence);
        Assert.Equal(RunStatus.Running, run.Snapshot.Status);
    }

    [Fact]
    public void GapDuplicateWrongRunAndInvalidTransitionLeaveStateUntouched()
    {
        var diagnostics = new List<string>();
        var run = Create(diagnostics.Add);
        var initial = run.Snapshot;
        var valid = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, Now);
        Assert.Throws<InvalidOperationException>(() => run.Apply(valid with { Sequence = 2 }));
        Assert.Throws<InvalidOperationException>(() => run.Apply(valid with { RunId = Guid.CreateVersion7() }));
        Assert.Throws<InvalidOperationException>(() => run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Succeeded, Now));
        Assert.Same(initial, run.Snapshot);
        Assert.Empty(run.Events);
        run.Apply(valid);
        Assert.Throws<InvalidOperationException>(() => run.Apply(valid));
        Assert.Equal(4, diagnostics.Count);
    }

    [Fact]
    public void UnknownEntitiesNonUtcAndLateResultsAreRejected()
    {
        var run = Create(_ => { });
        Assert.Throws<InvalidOperationException>(() => run.Append(EventEntity.Task, "node/missing", 1, Now));
        Assert.Throws<InvalidOperationException>(() => run.Append((EventEntity)99, null, 1, Now));
        Assert.Throws<InvalidOperationException>(() => run.Apply(new(run.Snapshot.RunId, 1, Now.ToOffset(TimeSpan.FromHours(1)),
            EventEntity.Run, null, (int)RunStatus.Running)));
        run.Append(EventEntity.Run, null, (int)RunStatus.Aborted, Now);
        Assert.Throws<InvalidOperationException>(() => run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, Now));
        Assert.Equal(RunStatus.Aborted, run.Snapshot.Status);
    }

    private static RunState Create(Action<string>? log = null) => new(Guid.CreateVersion7(),
        [new("node", [], [new("task")])], new WorkflowMetadata("test", new Dictionary<string, string>
            { ["area"] = "checkout" }.ToImmutableSortedDictionary()), log ?? Assert.Fail);
}
