using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Scheduling;
using Wf.Engine.Core.Transitions;

namespace Wf.Engine.Core.Tests;

public sealed class FailurePolicyTests
{
    public static IEnumerable<object[]> PoliciesAndConditions =>
        from node in Enum.GetValues<FailurePolicy>()
        from task in Enum.GetValues<FailurePolicy>()
        from condition in Enum.GetValues<DependencyCondition>()
        select new object[] { node, task, condition };

    [Theory]
    [MemberData(nameof(PoliciesAndConditions))]
    public void EveryPolicyAndConditionCombinationPropagatesCorrectly(
        FailurePolicy nodePolicy, FailurePolicy taskPolicy, DependencyCondition condition)
    {
        var node = new NodeDefinition("source", [], [new("fail", taskPolicy), new("sibling")], failure: nodePolicy);
        var tasks = new Dictionary<string, ExecutionStatus> { ["fail"] = ExecutionStatus.Running, ["sibling"] = ExecutionStatus.Pending };
        var plan = FailurePropagation.Plan(node, "fail", tasks);
        foreach (var change in plan.Changes)
            tasks[change.Task] = change.Status;
        if (taskPolicy == FailurePolicy.Soft)
        {
            Assert.Equal(ExecutionStatus.Pending, tasks["sibling"]);
            Assert.Null(FailurePropagation.ResolveNode(node, tasks));
            tasks["sibling"] = ExecutionStatus.Succeeded;
        }
        else
            Assert.Equal(ExecutionStatus.Cancelled, tasks["sibling"]);
        var result = FailurePropagation.ResolveNode(node, tasks)!.Value;
        var soft = nodePolicy == FailurePolicy.Soft || taskPolicy == FailurePolicy.Soft;
        Assert.Equal(soft ? ExecutionStatus.SoftFailed : ExecutionStatus.Failed, result);
        var reader = new NodeDefinition("reader", [new("source", condition)], []);
        var changes = ReadinessEvaluator.Evaluate([node, reader],
            new Dictionary<string, ExecutionStatus> { ["source"] = result, ["reader"] = ExecutionStatus.Pending });
        var expectedReady = condition == DependencyCondition.Always ||
            (condition == DependencyCondition.OnSuccess ? soft : !soft);
        Assert.Equal(expectedReady ? ExecutionStatus.Ready : ExecutionStatus.Skipped, Assert.Single(changes).Status);
    }

    [Fact]
    public void HardFailureRequestsCancellationButWaitsForRunningSiblingResults()
    {
        var node = new NodeDefinition("parallel", [], [new("fail"), new("running"), new("dispatched"), new("finished"), new("retry")]);
        var states = new Dictionary<string, ExecutionStatus>
        {
            ["fail"] = ExecutionStatus.Running, ["running"] = ExecutionStatus.Running,
            ["dispatched"] = ExecutionStatus.Dispatched, ["finished"] = ExecutionStatus.Succeeded,
            ["retry"] = ExecutionStatus.Retrying
        };
        var plan = FailurePropagation.Plan(node, "fail", states);
        Assert.Equal(["running", "dispatched"], plan.CancelRunning);
        foreach (var change in plan.Changes)
            states[change.Task] = change.Status;
        Assert.Null(FailurePropagation.ResolveNode(node, states));
        states["running"] = ExecutionStatus.Cancelled;
        states["dispatched"] = ExecutionStatus.Succeeded;
        Assert.Equal(ExecutionStatus.Failed, FailurePropagation.ResolveNode(node, states));
        Assert.Equal(ExecutionStatus.Succeeded, states["finished"]);
        Assert.Equal(ExecutionStatus.Cancelled, states["retry"]);
    }

    [Theory]
    [InlineData(FailurePolicy.Hard, ExecutionStatus.TimedOut)]
    [InlineData(FailurePolicy.Soft, ExecutionStatus.SoftFailed)]
    public void TimeoutObeysTaskPolicy(FailurePolicy policy, ExecutionStatus expected)
    {
        var node = new NodeDefinition("node", [], [new("task", policy)]);
        var states = new Dictionary<string, ExecutionStatus> { ["task"] = ExecutionStatus.Running };
        var change = Assert.Single(FailurePropagation.Plan(node, "task", states, timedOut: true).Changes);
        Assert.Equal(expected, change.Status);
    }

    [Fact]
    public void AggregateDistinguishesEmptySkippedCancelledAndSuccess()
    {
        Assert.Equal(ExecutionStatus.Succeeded, FailurePropagation.ResolveNode(new("empty", [], []),
            new Dictionary<string, ExecutionStatus>()));
        var node = new NodeDefinition("node", [], [new("task")]);
        foreach (var status in new[] { ExecutionStatus.Skipped, ExecutionStatus.Cancelled, ExecutionStatus.Succeeded })
            Assert.Equal(status, FailurePropagation.ResolveNode(node,
                new Dictionary<string, ExecutionStatus> { ["task"] = status }));
    }

    [Fact]
    public void MissingTaskStateAndLateFailureAreRejected()
    {
        var node = new NodeDefinition("node", [], [new("task")]);
        Assert.Throws<ArgumentException>(() => FailurePropagation.Plan(node, "task", new Dictionary<string, ExecutionStatus>()));
        Assert.Throws<InvalidOperationException>(() => FailurePropagation.Plan(node, "task",
            new Dictionary<string, ExecutionStatus> { ["task"] = ExecutionStatus.Succeeded }));
    }
}
