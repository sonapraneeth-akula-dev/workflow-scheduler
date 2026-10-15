using Wf.Sdk;

namespace Wf.Integration.Tests.Definition;

public sealed class NodeOutputsTests
{
    [Fact]
    public async Task TasksInOneNodeShareBoundedOutputsButOtherNodesStayIsolated()
    {
        var workflow = Workflow.Define("outputs", "1", w => w.Type("test").Node("node", n => n
            .Task("write", ctx => { ctx.Outputs["value"] = 42; })
            .Task("read", ctx => { Assert.Equal(42, ctx.Outputs.Get<int>("value")); })));
        var outputs = new NodeOutputs("node");
        foreach (var task in workflow.Nodes[0].Tasks)
            await workflow.Tasks[$"node/{task.Key}"](new(outputs: outputs));
        Assert.Empty(new TaskContext(outputs: new("other")).Outputs.Values);
    }

    [Fact]
    public async Task DeclaredDependentReadsTypedOutputFromProducer()
    {
        var result = 0;
        var workflow = Workflow.Define("inputs", "1", w => w.Type("test")
            .Node("producer", n => n.Task("write", ctx => { ctx.Outputs["number"] = 42; }))
            .Node("consumer", n => n.DependsOn("producer").Input("producer", "number")
                .Task("read", ctx => { result = ctx.Input<int>("producer", "number"); })));
        var outputs = workflow.Nodes.ToDictionary(n => n.Key, n => new NodeOutputs(n.Key));
        foreach (var node in workflow.Nodes)
            foreach (var task in node.Tasks)
                await workflow.Tasks[$"{node.Key}/{task.Key}"](new(outputs: outputs[node.Key],
                    inputs: outputs, declaredInputs: node.Inputs));
        Assert.Equal(42, result);
    }
}
