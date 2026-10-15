using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class FailurePolicyTests
{
    [Fact]
    public void HardAndOnSuccessAreDefaults()
    {
        var workflow = Workflow.Define("policies", "1", w => w.Type("test")
            .Node("first", n => n.Task("work", _ => { }))
            .Node("next", n => n.DependsOn("first")));
        Assert.All(workflow.Nodes, n => Assert.Equal(Failure.Hard, n.Failure));
        Assert.Equal(Failure.Hard, workflow.Nodes[0].Tasks[0].Failure);
        Assert.Equal(DependencyCondition.OnSuccess, workflow.Nodes[1].Dependencies[0].When);
    }

    [Theory]
    [InlineData(FailurePolicy.Hard, DependencyCondition.OnSuccess)]
    [InlineData(FailurePolicy.Hard, DependencyCondition.OnFailure)]
    [InlineData(FailurePolicy.Hard, DependencyCondition.Always)]
    [InlineData(FailurePolicy.Soft, DependencyCondition.OnSuccess)]
    [InlineData(FailurePolicy.Soft, DependencyCondition.OnFailure)]
    [InlineData(FailurePolicy.Soft, DependencyCondition.Always)]
    public void PoliciesAndConditionsSurviveContractSerialization(FailurePolicy failure, DependencyCondition when)
    {
        var workflow = Workflow.Define("policies", "1", w => w.Type("test")
            .Node("first", n => n.Task("work", _ => Task.CompletedTask, failure), failure)
            .Node("next", n => n.DependsOn("first", when: when).OnFailure(failure)));
        var nodes = JsonSerializer.Deserialize<NodeDefinition[]>(JsonSerializer.Serialize(workflow.Nodes))!;
        Assert.Equal(failure, nodes[0].Failure);
        Assert.Equal(failure, nodes[0].Tasks[0].Failure);
        Assert.Equal(failure, nodes[1].Failure);
        Assert.Equal(when, nodes[1].Dependencies[0].When);
    }

    [Fact]
    public void PolicyOverridesRemainIndependentForNodeAndTasks()
    {
        var node = Assert.Single(Workflow.Define("policies", "1", w => w.Type("test")
            .Node("node", n => n.OnFailure(Failure.Soft)
                .Task("hard", _ => { }).Task("soft", _ => { }, failure: Failure.Soft))).Nodes);
        Assert.Equal(Failure.Soft, node.Failure);
        Assert.Equal(Failure.Hard, node.Tasks[0].Failure);
        Assert.Equal(Failure.Soft, node.Tasks[1].Failure);
    }

    [Fact]
    public void UnknownPoliciesAndConditionsFailAtBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskDefinition("task", (FailurePolicy)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeDefinition("node", [], [], failure: (FailurePolicy)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DependencyDefinition("node", (DependencyCondition)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Workflow.Define("policy", "1", w => w.Type("test")
            .Node("node", n => n.OnFailure((FailurePolicy)99))));
    }

    [Fact]
    public void ConflictingDuplicateDependenciesFailInsteadOfCreatingAnImpossibleCondition()
    {
        Assert.Contains("first", Assert.Throws<ArgumentException>(() => Workflow.Define("policies", "1",
            w => w.Type("test").Node("first", _ => { }).Node("next", n => n
                .DependsOn("first").DependsOn("first", DependencyCondition.OnFailure)))).Message);
    }
}
