using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;

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
}
