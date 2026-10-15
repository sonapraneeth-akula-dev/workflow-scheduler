using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;
using Wf.Validation;

namespace Wf.Integration.Tests.Definition;

public sealed class VariableSchemaTests
{
    [Fact]
    public void SchemaJsonRoundTripRetainsTypesConstraintsAndNameOnlySecrets()
    {
        var workflow = Workflow.Define("vars", "1", w => w.Variables(v => v
            .String("env", required: true, allowed: ["dev", "prod"])
            .Secret("token", "deployment-token").Duration("delay", TimeSpan.FromMinutes(2))));
        var json = JsonSerializer.Serialize(workflow.Variables);
        var schemas = JsonSerializer.Deserialize<VariableSchema[]>(json)!;
        Assert.True(schemas[0].Required);
        Assert.Equal(["dev", "prod"], schemas[0].AllowedValues);
        Assert.Equal("deployment-token", schemas[1].Default!.Value.GetProperty("Name").GetString());
        Assert.Equal(VariableType.Duration, schemas[2].Type);
        Assert.Equal("00:02:00", schemas[2].Default!.Value.GetString());
    }

    [Fact]
    public async Task BoundVariablesReachEveryStageAndTaskWithoutCoercion()
    {
        var seen = new List<int>();
        Action<TaskContext> capture = ctx => seen.Add(ctx.Var<int>("count"));
        var workflow = Workflow.Define("vars", "1", w => w
            .Variables(v => v.Int("count", 3, required: true))
            .SystemInit(capture).PreSetup(capture)
            .Node("node", n => n.Task("work", capture))
            .PostProcess(capture).SystemPostProcess(capture));
        var bound = VariableBinder.Bind(workflow.Variables, new Dictionary<string, JsonElement>());
        foreach (var node in workflow.Nodes)
            foreach (var task in node.Tasks)
                await workflow.Tasks[$"{node.Key}/{task.Key}"](new(variables: bound));
        Assert.Equal([3, 3, 3, 3, 3], seen);
        Assert.Throws<ArgumentException>(() => VariableBinder.Bind(workflow.Variables,
            new Dictionary<string, JsonElement> { ["count"] = JsonSerializer.SerializeToElement("wrong") }));
    }
}
