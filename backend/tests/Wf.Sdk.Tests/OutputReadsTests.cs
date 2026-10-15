using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class OutputReadsTests
{
    [Theory]
    [InlineData(DependencyCondition.OnSuccess)]
    [InlineData(DependencyCondition.OnFailure)]
    [InlineData(DependencyCondition.Always)]
    public void DirectDependencyReadsAreAcceptedAndSerialized(DependencyCondition condition)
    {
        var workflow = Workflow.Define("inputs", "1", w => w.Type("test")
            .Node("source", _ => { }).Node("reader", n => n.DependsOn("source", condition).Input("source", "version")));
        var node = JsonSerializer.Deserialize<NodeDefinition>(JsonSerializer.Serialize(workflow.Nodes[1]))!;
        Assert.Equal(new InputBinding("source", "version"), Assert.Single(node.Inputs));
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("missing")]
    [InlineData("reader")]
    public void InvalidReadNamesReaderAndSource(string source)
    {
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("inputs", "1", w => w.Type("test")
            .Node("unrelated", _ => { }).Node("reader", n => n.Input(source, "value"))));
        Assert.Contains("reader", error.Message);
        Assert.Contains(source, error.Message);
    }

    [Fact]
    public void TransitiveReadNeedsAnExplicitDependency()
    {
        Assert.Throws<ArgumentException>(() => Workflow.Define("inputs", "1", w => w.Type("test")
            .Node("root", _ => { }).Node("middle", n => n.DependsOn("root"))
            .Node("reader", n => n.DependsOn("middle").Input("root", "value"))));
    }

    [Fact]
    public void DuplicateBindingsFail()
    {
        Assert.Throws<ArgumentException>(() => Workflow.Define("inputs", "1", w => w.Type("test")
            .Node("source", _ => { }).Node("reader", n => n.DependsOn("source")
                .Input("source", "value").Input("source", "value"))));
    }

    [Fact]
    public void ContextRejectsUndeclaredAndUnavailableReads()
    {
        var outputs = new NodeOutputs("source");
        outputs["value"] = 42;
        var context = new TaskContext(inputs: new Dictionary<string, NodeOutputs> { ["source"] = outputs },
            declaredInputs: [new("source", "allowed"), new("missing", "value")]);
        Assert.Throws<InvalidOperationException>(() => context.Input<int>("source", "value"));
        Assert.Throws<KeyNotFoundException>(() => context.Input<int>("source", "allowed"));
        Assert.Throws<KeyNotFoundException>(() => context.Input<int>("missing", "value"));
    }
}
