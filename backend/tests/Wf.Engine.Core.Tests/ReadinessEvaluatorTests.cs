using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Scheduling;
using Wf.Engine.Core.Stages;

namespace Wf.Engine.Core.Tests;

public sealed class ReadinessEvaluatorTests
{
    public static IEnumerable<object[]> ConditionsAndStates =>
        from condition in Enum.GetValues<DependencyCondition>()
        from status in Enum.GetValues<ExecutionStatus>()
        select new object[] { condition, status };

    [Theory]
    [MemberData(nameof(ConditionsAndStates))]
    public void EveryConditionStateCombinationHasExplicitSemantics(DependencyCondition condition, ExecutionStatus status)
    {
        var expected = condition switch
        {
            DependencyCondition.OnSuccess => status is ExecutionStatus.Succeeded or ExecutionStatus.SoftFailed or ExecutionStatus.Reused,
            DependencyCondition.OnFailure => status is ExecutionStatus.Failed or ExecutionStatus.TimedOut,
            DependencyCondition.Always => status is ExecutionStatus.Succeeded or ExecutionStatus.SoftFailed or ExecutionStatus.Reused or
                ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Cancelled or ExecutionStatus.Skipped,
            _ => false
        };
        Assert.Equal(expected, ReadinessEvaluator.IsSatisfied(condition, status));
    }

    [Fact]
    public void FanOutBranchesAreReadyTogetherAndDiamondWaitsForBoth()
    {
        ImmutableArray<NodeDefinition> nodes = [
            new("root", [], []), new("left", [new("root")], []),
            new("right", [new("root")], []), new("end", [new("left"), new("right")], [])];
        var states = nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        Assert.Equal(new NodeStateChange("root", ExecutionStatus.Ready), Assert.Single(ReadinessEvaluator.Evaluate(nodes, states)));
        states["root"] = ExecutionStatus.Succeeded;
        Assert.Equal(["left", "right"], ReadinessEvaluator.Evaluate(nodes, states).Select(c => c.Node));
        states["left"] = ExecutionStatus.Succeeded;
        states["right"] = ExecutionStatus.Running;
        Assert.Empty(ReadinessEvaluator.Evaluate(nodes, states));
        states["right"] = ExecutionStatus.Succeeded;
        Assert.Equal(new NodeStateChange("end", ExecutionStatus.Ready), Assert.Single(ReadinessEvaluator.Evaluate(nodes, states)));
    }

    [Fact]
    public void FailureSkipsDefaultDependentButRunsFailureAndAlwaysBranches()
    {
        ImmutableArray<NodeDefinition> nodes = [new("source", [], []),
            new("success", [new("source")], []),
            new("failure", [new("source", DependencyCondition.OnFailure)], []),
            new("always", [new("source", DependencyCondition.Always)], [])];
        var states = nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        states["source"] = ExecutionStatus.Failed;
        Assert.Equal([
            new NodeStateChange("success", ExecutionStatus.Skipped),
            new NodeStateChange("failure", ExecutionStatus.Ready),
            new NodeStateChange("always", ExecutionStatus.Ready)], ReadinessEvaluator.Evaluate(nodes, states));
    }

    [Fact]
    public void EvenImpossibleConditionWaitsForAllDependenciesBeforeSkipping()
    {
        ImmutableArray<NodeDefinition> nodes = [new("failed", [], []), new("running", [], []),
            new("dependent", [new("failed"), new("running")], [])];
        var states = new Dictionary<string, ExecutionStatus>
            { ["failed"] = ExecutionStatus.Failed, ["running"] = ExecutionStatus.Running, ["dependent"] = ExecutionStatus.Pending };
        Assert.Empty(ReadinessEvaluator.Evaluate(nodes, states));
        states["running"] = ExecutionStatus.Succeeded;
        Assert.Equal(ExecutionStatus.Skipped, Assert.Single(ReadinessEvaluator.Evaluate(nodes, states)).Status);
    }

    [Fact]
    public void StageEligibilityRestrictsSchedulingAndReadyNodesAreNotReemitted()
    {
        ImmutableArray<NodeDefinition> nodes = [new("$init", [], []), new("body", [], [])];
        var states = nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        Assert.Equal("body", Assert.Single(ReadinessEvaluator.Evaluate(nodes, states)).Node);
        Assert.Equal("$init", Assert.Single(ReadinessEvaluator.Evaluate(nodes, states, ["$init"])).Node);
        states["$init"] = ExecutionStatus.Ready;
        Assert.Empty(ReadinessEvaluator.Evaluate(nodes, states, ["$init"]));
    }

    [Fact]
    public void MissingOrUnknownStatesFailInsteadOfDefaultingToPending()
    {
        Assert.Throws<ArgumentException>(() => ReadinessEvaluator.Evaluate([new("node", [], [])],
            new Dictionary<string, ExecutionStatus>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadinessEvaluator.IsSatisfied((DependencyCondition)99, ExecutionStatus.Succeeded));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadinessEvaluator.IsSatisfied(DependencyCondition.Always, (ExecutionStatus)99));
    }
}
