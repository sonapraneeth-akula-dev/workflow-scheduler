using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Integration.Tests.Definition;

public sealed class PolicyTests
{
    [Fact]
    public void ResolvedTaskPolicySurvivesContractRoundTripWithoutReexecutingAuthorCode()
    {
        var calls = 0;
        var workflow = Workflow.Define("policy", "1", w => w.Type("test").Defaults(d => d.Retry(2))
            .Node("node", n => n.Timeout(TimeSpan.FromMinutes(2))
                .Task("task", _ => { calls++; }, t => t.NoAutoRetry())));
        var node = JsonSerializer.Deserialize<NodeDefinition>(JsonSerializer.Serialize(workflow.Nodes[0]))!;
        Assert.Equal(TimeSpan.FromMinutes(2), node.Tasks[0].Policy.AttemptTimeout);
        Assert.Equal(2, node.Tasks[0].Policy.Retry.Retries);
        Assert.False(node.Tasks[0].Policy.AutoRetry);
        Assert.Equal(0, calls);
    }
}
