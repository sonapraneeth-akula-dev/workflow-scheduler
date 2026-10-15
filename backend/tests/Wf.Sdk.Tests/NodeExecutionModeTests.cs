using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class NodeExecutionModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(4)]
    public void ParallelPreservesOptionalLimitAndTaskOrder(int? limit)
    {
        var node = Assert.Single(Workflow.Define("sample", "1", w => w.Node("node", n => n
            .Parallel(limit).Task("z", _ => { }).Task("a", _ => { }).Task("m", _ => { }))).Nodes);
        Assert.Equal(NodeExecutionMode.Parallel, node.Mode);
        Assert.Equal(limit, node.MaxParallel);
        Assert.Equal(["z", "a", "m"], node.Tasks.Select(t => t.Key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void InvalidParallelLimitIsRejectedByBuilderAndContract(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Workflow.Define("sample", "1", w =>
            w.Node("node", n => n.Parallel(limit))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeDefinition("node", [], [],
            NodeExecutionMode.Parallel, limit));
    }

    [Fact]
    public void SeriesResetsPreviouslyDeclaredParallelLimit()
    {
        var node = Assert.Single(Workflow.Define("sample", "1", w => w
            .Node("node", n => n.Parallel(2).Series())).Nodes);
        Assert.Equal(NodeExecutionMode.Series, node.Mode);
        Assert.Null(node.MaxParallel);
    }

    [Fact]
    public void ContractRejectsLimitForSeriesAndUnknownMode()
    {
        Assert.Throws<ArgumentException>(() => new NodeDefinition("node", [], [], NodeExecutionMode.Series, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeDefinition("node", [], [], (NodeExecutionMode)42));
    }

    [Fact]
    public void ContractDefaultsToSeriesAndRejectsUninitializedCollections()
    {
        var node = new NodeDefinition("node", [], []);
        Assert.Equal(NodeExecutionMode.Series, node.Mode);
        Assert.Null(node.MaxParallel);
        Assert.Throws<ArgumentException>(() => new NodeDefinition("node", default, []));
        Assert.Throws<ArgumentException>(() => new NodeDefinition("node", [], default));
    }
}
