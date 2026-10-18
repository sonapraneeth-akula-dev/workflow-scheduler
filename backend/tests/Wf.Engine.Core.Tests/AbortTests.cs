using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core.Abort;
using Wf.Engine.Core.Stages;

namespace Wf.Engine.Core.Tests;

public sealed class AbortTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly AbortRecord Record = new("operator", "Operator", Now, "Stop requested", AbortSource.CLI);

    [Fact]
    public void QueuedAbortSkipsEveryEntityAndStopsImmediately()
    {
        var run = Create([Node("node"), Node("$post")]);
        var plan = AbortHandler.Request(run.Snapshot, Record);
        Assert.Equal(RunStatus.Queued, run.Snapshot.Status);
        Apply(run, plan, Now);
        Assert.Equal(RunStatus.Aborted, run.Snapshot.Status);
        Assert.Equal(Now, run.Snapshot.EndedAt);
        Assert.All(run.Snapshot.Tasks.Values, state => Assert.Equal(ExecutionStatus.Skipped, state));
        Assert.All(run.Snapshot.Nodes.Values, state => Assert.Equal(ExecutionStatus.Skipped, state));
        Assert.Same(Record, run.Snapshot.Abort);
        Assert.Empty(AbortHandler.Request(run.Snapshot, new("other", "Other", Now, "Second", AbortSource.API)).Tasks);
        Assert.Single(run.Events, e => e.Abort is not null);
    }

    [Fact]
    public void RunningAbortCancelsActiveSkipsWaitingAndPreservesCleanup()
    {
        ImmutableArray<NodeDefinition> nodes = [Node("active"), Node("pending"), Node("$post"),
            new("always", [new("active", DependencyCondition.Always)], [new("work")])];
        var run = Create(nodes);
        StartRun(run);
        StartNode(run, "active");
        StartTask(run, "active/work");
        Apply(run, AbortHandler.Request(run.Snapshot, Record), Now);
        var plan = AbortHandler.Plan(nodes, run.Snapshot);
        Assert.Equal(["active/work"], plan.CancelRunning);
        Assert.Null(plan.Status);
        Apply(run, plan, Now);
        Assert.Equal(ExecutionStatus.Running, run.Snapshot.Nodes["active"]);
        Assert.Equal(ExecutionStatus.Skipped, run.Snapshot.Nodes["pending"]);
        Assert.Equal(ExecutionStatus.Skipped, run.Snapshot.Tasks["pending/work"]);
        Assert.Equal(ExecutionStatus.Pending, run.Snapshot.Nodes["always"]);
        Assert.Equal(ExecutionStatus.Pending, run.Snapshot.Nodes["$post"]);

        run.Append(EventEntity.Task, "active/work", (int)ExecutionStatus.Cancelled, Now.AddSeconds(1));
        Apply(run, AbortHandler.Plan(nodes, run.Snapshot), Now.AddSeconds(1));
        var stages = StagePlanner.Plan(nodes, run.Snapshot.Nodes, run.Snapshot.Status);
        Assert.Equal(["always"], stages.EligibleNodes);
        Succeed(run, "always", Now.AddSeconds(2));
        Succeed(run, "$post", Now.AddSeconds(3));
        Apply(run, AbortHandler.Plan(nodes, run.Snapshot), Now.AddSeconds(3));
        Assert.Equal(RunStatus.Aborted, run.Snapshot.Status);
        Assert.Equal(Now.AddSeconds(3), run.Snapshot.EndedAt);
    }

    [Fact]
    public void AbortDuringBackoffSkipsRetryWithoutDispatchingAgain()
    {
        ImmutableArray<NodeDefinition> nodes = [Node("node")];
        var run = Create(nodes);
        StartRun(run);
        StartNode(run, "node");
        StartTask(run, "node/work");
        run.Append(EventEntity.Task, "node/work", (int)ExecutionStatus.Retrying, Now);
        Apply(run, AbortHandler.Request(run.Snapshot, Record), Now);
        var plan = AbortHandler.Plan(nodes, run.Snapshot);
        Assert.Empty(plan.CancelRunning);
        Apply(run, plan, Now.AddSeconds(1));
        Assert.Equal(ExecutionStatus.Skipped, run.Snapshot.Tasks["node/work"]);
        Assert.Equal(ExecutionStatus.Cancelled, run.Snapshot.Nodes["node"]);
        Assert.Equal(RunStatus.Aborted, run.Snapshot.Status);
    }

    [Theory]
    [InlineData("$post")]
    [InlineData("always")]
    public void CleanupAlreadyRunningAtAbortIsCancelledButLaterCleanupCanStart(string activeKey)
    {
        var active = activeKey == "always"
            ? new NodeDefinition("always", [new("body", DependencyCondition.Always)], [new("work"), new("later")])
            : Node("$post", "work", "later");
        ImmutableArray<NodeDefinition> nodes = [Node("body"), active, Node("$sys-post")];
        var run = Create(nodes);
        StartRun(run);
        Succeed(run, "body", Now);
        StartNode(run, activeKey);
        StartTask(run, $"{activeKey}/work");
        Apply(run, AbortHandler.Request(run.Snapshot, Record), Now);
        var plan = AbortHandler.Plan(nodes, run.Snapshot);
        Assert.Equal([$"{activeKey}/work"], plan.CancelRunning);
        Apply(run, plan, Now);
        Assert.Equal(ExecutionStatus.Skipped, run.Snapshot.Tasks[$"{activeKey}/later"]);
        run.Append(EventEntity.Task, $"{activeKey}/work", (int)ExecutionStatus.Cancelled, Now);
        Apply(run, AbortHandler.Plan(nodes, run.Snapshot), Now);
        StartNode(run, "$sys-post");
        StartTask(run, "$sys-post/work");
        var later = AbortHandler.Plan(nodes, run.Snapshot);
        Assert.Empty(later.CancelRunning);
        Assert.Null(later.Status);
        run.Append(EventEntity.Task, "$sys-post/work", (int)ExecutionStatus.Succeeded, Now.AddSeconds(2));
        run.Append(EventEntity.Node, "$sys-post", (int)ExecutionStatus.Succeeded, Now.AddSeconds(2));
        Apply(run, AbortHandler.Plan(nodes, run.Snapshot), Now.AddSeconds(2));
        Assert.Equal(RunStatus.Aborted, run.Snapshot.Status);
    }

    [Fact]
    public void SnapshotAndEventReplayKeepOriginalAbortTargetsAndActualStopTime()
    {
        ImmutableArray<NodeDefinition> nodes = [Node("node")];
        var run = Create(nodes);
        var initial = run.Snapshot;
        StartRun(run);
        StartNode(run, "node");
        StartTask(run, "node/work");
        Apply(run, AbortHandler.Request(run.Snapshot, Record), Now);
        var checkpoint = JsonSerializer.Deserialize<RunSnapshot>(JsonSerializer.Serialize(run.Snapshot))!;
        var restored = RunState.Restore(checkpoint, [], Assert.Fail);
        Assert.Equal(["node/work"], AbortHandler.Plan(nodes, restored.Snapshot).CancelRunning);
        Assert.Null(AbortHandler.Request(restored.Snapshot,
            new("second", "Second", Now.AddSeconds(1), "Duplicate", AbortSource.UI)).Status);
        restored.Append(EventEntity.Task, "node/work", (int)ExecutionStatus.Cancelled, Now.AddSeconds(5));
        Apply(restored, AbortHandler.Plan(nodes, restored.Snapshot), Now.AddSeconds(5));
        var replay = RunState.Restore(initial, run.Events.Concat(restored.Events), Assert.Fail);
        Assert.Equal(JsonSerializer.Serialize(restored.Snapshot), JsonSerializer.Serialize(replay.Snapshot));
        Assert.Equal(Now.AddSeconds(5), replay.Snapshot.EndedAt);
        Assert.Single(run.Events.Concat(restored.Events), e => e.Abort is not null);
        Assert.Single(run.Events.Concat(restored.Events), e => e.Entity == EventEntity.Run &&
            e.State == (int)RunStatus.Aborted);
    }

    [Fact]
    public void CompletedRunRejectsAbortAndRecordCannotBeReplacedOrAppliedToOtherEntities()
    {
        var run = Create([Node("node")], _ => { });
        StartRun(run);
        Apply(run, AbortHandler.Request(run.Snapshot, Record), Now);
        Assert.Throws<InvalidOperationException>(() =>
            run.CreateEvent(EventEntity.Task, "node/work", (int)ExecutionStatus.Skipped, Now, Record));
        Assert.Throws<InvalidOperationException>(() =>
            run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Aborted, Now, Record));
        Assert.Throws<InvalidOperationException>(() =>
            run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Aborted, Now.AddTicks(-1)));
        Apply(run, AbortHandler.Plan([Node("node")], run.Snapshot), Now);
        var success = Create([]);
        StartRun(success);
        success.Append(EventEntity.Run, null, (int)RunStatus.Succeeded, Now);
        Assert.Throws<InvalidOperationException>(() => AbortHandler.Request(success.Snapshot, Record));
        Assert.Throws<InvalidOperationException>(() => AbortHandler.Plan([], success.Snapshot));
    }

    [Fact]
    public void InvalidRecordIsRejectedAtBoundary()
    {
        Assert.Throws<ArgumentException>(() => new AbortRecord("operator", "Operator", Now, " ", AbortSource.CLI));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AbortRecord("operator", "Operator", Now, new('x', 501), AbortSource.CLI));
        Assert.Equal(500, new AbortRecord("operator", "Operator", Now, new('x', 500), AbortSource.CLI).Reason.Length);
        Assert.Throws<ArgumentException>(() => new AbortRecord("", "Operator", Now, "Stop", AbortSource.CLI));
        Assert.Throws<ArgumentException>(() => new AbortRecord("operator", "", Now, "Stop", AbortSource.CLI));
        Assert.Throws<ArgumentException>(() => new AbortRecord("operator", "Operator", Now.ToOffset(TimeSpan.FromHours(1)), "Stop", AbortSource.CLI));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AbortRecord("operator", "Operator", Now, "Stop", (AbortSource)99));
    }

    private static NodeDefinition Node(string key, params string[] tasks) =>
        new(key, [], (tasks.Length == 0 ? ["work"] : tasks).Select(t => new TaskDefinition(t)).ToImmutableArray());
    private static RunState Create(ImmutableArray<NodeDefinition> nodes, Action<string>? log = null) =>
        new(Guid.CreateVersion7(), nodes, new("test", ImmutableSortedDictionary<string, string>.Empty), log ?? Assert.Fail);
    private static void StartRun(RunState run) => run.Append(EventEntity.Run, null, (int)RunStatus.Running, Now);
    private static void StartNode(RunState run, string key)
    {
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            run.Append(EventEntity.Node, key, (int)state, Now);
    }
    private static void StartTask(RunState run, string key)
    {
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            run.Append(EventEntity.Task, key, (int)state, Now);
    }
    private static void Succeed(RunState run, string node, DateTimeOffset at)
    {
        StartNode(run, node);
        StartTask(run, $"{node}/work");
        run.Append(EventEntity.Task, $"{node}/work", (int)ExecutionStatus.Succeeded, at);
        run.Append(EventEntity.Node, node, (int)ExecutionStatus.Succeeded, at);
    }
    private static void Apply(RunState run, AbortPlan plan, DateTimeOffset at)
    {
        foreach (var task in plan.Tasks)
            run.Append(EventEntity.Task, task.Task, (int)task.Status, at);
        foreach (var node in plan.Nodes)
            run.Append(EventEntity.Node, node.Node, (int)node.Status, at);
        if (plan.Status is { } status)
            run.Append(EventEntity.Run, null, (int)status, at, plan.Record);
    }
}
