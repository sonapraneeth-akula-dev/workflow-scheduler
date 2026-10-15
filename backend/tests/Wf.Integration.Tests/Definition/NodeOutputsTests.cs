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
}
