using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Stages;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Tests;

public sealed class StageOrderTests
{
    private static readonly ImmutableArray<NodeDefinition> Nodes =
        new[] { "$sys-post", "work", "$post", "$pre", "$init" }.Select(key => new NodeDefinition(key, [], [])).ToImmutableArray();

    [Fact]
    public void StagesUseCanonicalOrderNotDeclarationOrderAndEmitNodeChanges()
    {
        var states = Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        var visited = new List<string>();
        while (true)
        {
            var plan = StagePlanner.Plan(Nodes, states, RunStatus.Running);
            foreach (var change in plan.Changes)
            {
                TransitionTable.Require(states[change.Node], change.Status, Assert.Fail);
                states[change.Node] = change.Status;
            }
            if (plan.Complete)
                break;
            var key = Assert.Single(plan.EligibleNodes);
            visited.Add(key);
            states[key] = ExecutionStatus.Succeeded;
        }
        Assert.Equal(["$init", "$pre", "work", "$post", "$sys-post"], visited);
    }

    [Theory]
    [InlineData("$init")]
    [InlineData("$pre")]
    [InlineData("work")]
    [InlineData("$post")]
    public void PostStagesAlwaysRunAfterHardFailure(string failed)
    {
        var states = Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        if (failed is "work" or "$post")
        {
            states["$init"] = ExecutionStatus.Succeeded;
            states["$pre"] = ExecutionStatus.Succeeded;
        }
        if (failed == "$post")
            states["work"] = ExecutionStatus.Succeeded;
        states[failed] = ExecutionStatus.Failed;
        var plan = StagePlanner.Plan(Nodes, states, RunStatus.Running);
        Assert.Equal(failed == "$post" ? "$sys-post" : "$post", Assert.Single(plan.EligibleNodes));
        Assert.DoesNotContain(plan.Changes, change => change.Node is "$post" or "$sys-post" &&
            change.Status == ExecutionStatus.Skipped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbortAndTimeoutSkipUnstartedWorkThenRunPostStages(bool timeout)
    {
        var states = Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        var plan = StagePlanner.Plan(Nodes, states, timeout ? RunStatus.Running : RunStatus.Aborting, timeout);
        Assert.Equal("$post", Assert.Single(plan.EligibleNodes));
        Assert.Equal(3, plan.Changes.Count(c => c.Status == ExecutionStatus.Skipped));
        Assert.Contains(new NodeStateChange("$post", ExecutionStatus.Ready), plan.Changes);
    }

    [Fact]
    public void CleanupWaitsUntilRunningOrDispatchedWorkHasStopped()
    {
        var states = Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        states["work"] = ExecutionStatus.Running;
        Assert.Empty(StagePlanner.Plan(Nodes, states, RunStatus.Aborting).EligibleNodes);
        states["work"] = ExecutionStatus.Cancelled;
        Assert.Equal("$post", Assert.Single(StagePlanner.Plan(Nodes, states, RunStatus.Aborting).EligibleNodes));
    }

    [Fact]
    public void AbortPreservesAlwaysNodesBeforePostStages()
    {
        var nodes = Nodes.Add(new("cleanup", [new("work", DependencyCondition.Always)], []));
        var states = nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        var plan = StagePlanner.Plan(nodes, states, RunStatus.Aborting);
        Assert.Equal("cleanup", Assert.Single(plan.EligibleNodes));
        Assert.DoesNotContain(plan.Changes, c => c.Node == "cleanup" && c.Status == ExecutionStatus.Skipped);
    }

    [Fact]
    public void SoftFailureAllowsNextStageAndMissingStagesAreOptional()
    {
        var states = Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        states["$init"] = ExecutionStatus.SoftFailed;
        Assert.Equal("$pre", Assert.Single(StagePlanner.Plan(Nodes, states, RunStatus.Running).EligibleNodes));
        Assert.True(StagePlanner.Plan([], new Dictionary<string, ExecutionStatus>(), RunStatus.Running).Complete);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Aborted)]
    [InlineData(RunStatus.TimedOut)]
    public void TerminalRunCannotRestartStages(RunStatus status)
    {
        var states = Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        var plan = StagePlanner.Plan(Nodes, states, status);
        Assert.True(plan.Complete);
        Assert.Empty(plan.EligibleNodes);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void MissingStateFailsExplicitly() =>
        Assert.Throws<ArgumentException>(() => StagePlanner.Plan(Nodes, new Dictionary<string, ExecutionStatus>(), RunStatus.Running));
}
