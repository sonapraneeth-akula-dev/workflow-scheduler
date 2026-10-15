using System.Text.Json;
using Wf.Sdk;
using Wf.Validation;

namespace Wf.Integration.Tests.Execution;

public sealed class TaskRegistryTests
{
    [Fact]
    public async Task RegistryPreservesStageIdentityBoundVariablesAndDeclaredInputFlow()
    {
        var seen = new List<string>();
        var workflow = Workflow.Define("context", "1", w => w.Type("test").Variables(v => v.Int("count", 42))
            .PreSetup(ctx => { ctx.Log(ctx.TaskKey!); ctx.Outputs["count"] = ctx.Var<int>("count"); })
            .Node("read", n => n.DependsOn("$pre").Input("$pre", "count")
                .Task("copy", ctx => { ctx.Log(ctx.TaskKey!); ctx.Outputs["copied"] = ctx.Input<int>("$pre", "count"); })));
        var variables = VariableBinder.Bind(workflow.Variables, new Dictionary<string, JsonElement>());
        var outputs = workflow.Nodes.ToDictionary(n => n.Key, n => new NodeOutputs(n.Key));
        var registry = new TaskRegistry(workflow);
        foreach (var node in workflow.Nodes)
            foreach (var task in node.Tasks)
                await registry.ExecuteAsync($"{node.Key}/{task.Key}", new(TestContext.Current.CancellationToken,
                    variables, outputs[node.Key], outputs, node.Inputs, seen.Add));
        Assert.Equal(["$pre/run", "read/copy"], seen);
        Assert.Equal(42, outputs["read"].Get<int>("copied"));
    }
}
