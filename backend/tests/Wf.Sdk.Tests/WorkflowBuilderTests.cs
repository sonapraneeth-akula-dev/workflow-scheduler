using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class WorkflowBuilderTests
{
    [Fact]
    public void BuildsOrderedNodesTasksAndDependenciesWithoutExecutingTasks()
    {
        var executed = false;
        var definition = Workflow.Define("payments/release/deploy", "1.0.0", w => w
            .Node("build", n => n.Task("restore", _ => executed = true).Task("compile", _ => { }))
            .Node("deploy", n => n.DependsOn("build").Parallel(2).Task("release", _ => Task.CompletedTask)));

        Assert.Equal("payments/release/deploy", definition.Name);
        Assert.Equal("1.0.0", definition.Version);
        Assert.Equal(["build", "deploy"], definition.Nodes.Select(n => n.Key));
        Assert.Equal(["restore", "compile"], definition.Nodes[0].Tasks.Select(t => t.Key));
        Assert.Equal(["build"], definition.Nodes[1].Dependencies);
        Assert.Equal(NodeExecutionMode.Series, definition.Nodes[0].Mode);
        Assert.Equal(NodeExecutionMode.Parallel, definition.Nodes[1].Mode);
        Assert.Equal(2, definition.Nodes[1].MaxParallel);
        Assert.False(executed);
    }

    [Fact]
    public void RetainedBuildersCannotMutatePublishedDefinition()
    {
        WorkflowBuilder? workflow = null;
        NodeBuilder? node = null;
        var definition = Workflow.Define("sample", "1.0.0", w =>
        {
            workflow = w;
            w.Node("first", n => { node = n; n.Task("one", _ => { }); });
        });
        workflow!.Node("second", n => n.Task("two", _ => { }));
        node!.DependsOn("later").Task("three", _ => { }).Parallel(4);

        var first = Assert.Single(definition.Nodes);
        Assert.Empty(first.Dependencies);
        Assert.Equal("one", Assert.Single(first.Tasks).Key);
        Assert.Equal(NodeExecutionMode.Series, first.Mode);
        Assert.Single(definition.Tasks);
    }

    [Fact]
    public async Task RegistersSyncAndAsyncDelegatesWithStableNodeTaskKeys()
    {
        var calls = new List<string>();
        var definition = Workflow.Define("sample", "1.0.0", w => w
            .Node("a", n => n.Task("work", _ => { calls.Add("sync"); }))
            .Node("b", n => n.Task("work", async ctx =>
            {
                await Task.Yield();
                Assert.False(ctx.CancellationToken.IsCancellationRequested);
                calls.Add("async");
            })));
        await definition.Tasks["a/work"](new());
        await definition.Tasks["b/work"](new());
        Assert.Equal(["sync", "async"], calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsEmptyNameOrVersion(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => Workflow.Define(value!, "1", _ => { }));
        Assert.ThrowsAny<ArgumentException>(() => Workflow.Define("name", value!, _ => { }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad/key")]
    public void RejectsInvalidNodeTaskAndDependencyKeys(string? key)
    {
        Assert.ThrowsAny<ArgumentException>(() => Workflow.Define("name", "1", w => w.Node(key!, _ => { })));
        Assert.ThrowsAny<ArgumentException>(() => Workflow.Define("name", "1", w =>
            w.Node("node", n => n.Task(key!, _ => { }))));
        Assert.ThrowsAny<ArgumentException>(() => Workflow.Define("name", "1", w =>
            w.Node("node", n => n.DependsOn(key!))));
    }

    [Fact]
    public void RejectsNullCallbacksAndDependencyArrays()
    {
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("name", "1", null!));
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("name", "1", w => w.Node("node", null!)));
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("name", "1", w =>
            w.Node("node", n => n.Task("task", (Action<TaskContext>)null!))));
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("name", "1", w =>
            w.Node("node", n => n.Task("task", (Func<TaskContext, Task>)null!))));
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("name", "1", w =>
            w.Node("node", n => n.DependsOn((string[])null!))));
    }
}
