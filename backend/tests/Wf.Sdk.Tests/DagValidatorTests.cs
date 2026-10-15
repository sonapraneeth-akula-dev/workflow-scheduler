using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class DagValidatorTests
{
    [Fact]
    public void AcceptsForwardReferencesDisconnectedNodesAndDiamond()
    {
        var workflow = Workflow.Define("dag", "1", w => w
            .Node("end", n => n.DependsOn("left", "right"))
            .Node("left", n => n.DependsOn("start"))
            .Node("right", n => n.DependsOn("start"))
            .Node("start", _ => { })
            .Node("independent", _ => { }));
        Assert.Equal(5, workflow.Nodes.Length);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("missing")]
    public void UnknownDependencyNamesReaderAndMissingKey(string dependency)
    {
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("dag", "1", w => w
            .Node("reader", n => n.DependsOn(dependency))));
        Assert.Contains("reader", error.Message);
        Assert.Contains(dependency, error.Message);
    }

    [Fact]
    public void DuplicateNodeNamesKeyEvenWithoutTasks()
    {
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("dag", "1", w => w
            .Node("duplicate", _ => { }).Node("duplicate", _ => { })));
        Assert.Contains("duplicate", error.Message);
    }

    [Fact]
    public void DuplicateTaskNamesNodeAndKey()
    {
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("dag", "1", w => w
            .Node("node", n => n.Task("duplicate", _ => { }).Task("duplicate", _ => { }))));
        Assert.Contains("node", error.Message);
        Assert.Contains("duplicate", error.Message);
    }

    [Fact]
    public void CycleNamesEveryMemberButNotDownstreamNodes()
    {
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("dag", "1", w => w
            .Node("downstream", n => n.DependsOn("a"))
            .Node("a", n => n.DependsOn("b"))
            .Node("b", n => n.DependsOn("c"))
            .Node("c", n => n.DependsOn("a"))));
        Assert.Contains("a -> b -> c -> a", error.Message);
        Assert.DoesNotContain("downstream", error.Message);
    }

    [Fact]
    public void SelfCycleIsRejected()
    {
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("dag", "1", w => w
            .Node("self", n => n.DependsOn("self"))));
        Assert.Contains("self -> self", error.Message);
    }

    [Fact]
    public void KeysAreOrdinalAndTaskKeysAreScopedToNode()
    {
        var workflow = Workflow.Define("dag", "1", w => w
            .Node("a", n => n.Task("same", _ => { }))
            .Node("A", n => n.DependsOn("a").Task("same", _ => { })));
        Assert.Equal(2, workflow.Tasks.Count);
    }

    [Fact]
    public void DeepGraphsDoNotUseCallStackForTraversal()
    {
        var workflow = Workflow.Define("deep", "1", w =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                var dependency = i + 1;
                w.Node($"n{i}", n => { if (dependency < 10_000) n.DependsOn($"n{dependency}"); });
            }
        });
        Assert.Equal(10_000, workflow.Nodes.Length);
    }
}
