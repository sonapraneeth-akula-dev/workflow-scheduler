using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;
using Wf.Validation;

namespace Wf.Integration.Tests.Definition;

public sealed class PolicyTests
{
    [Fact]
    public void PolicyRulesValidateResolvedStageAndTaskPoliciesAfterManifestRoundTrip()
    {
        var workflow = Workflow.Define("invalid-policies", "1", w => w.Type("test")
            .RunTimeout(TimeSpan.FromMinutes(10))
            .Defaults(d => d.Retry(11))
            .PostProcess(_ => { })
            .Node("node", n => n.Timeout(TimeSpan.FromMinutes(2))
                .Task("work", _ => { }, t => t.Timeout(TimeSpan.FromMinutes(3)))));
        var manifest = JsonSerializer.Deserialize(ManifestWriter.WriteUtf8(workflow),
            Wf.Contracts.Manifest.WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var errors = PolicyRules.Validate(manifest.Nodes, manifest.RunTimeout)
            .Where(d => d.Severity == PolicyDiagnosticSeverity.Error).ToArray();
        Assert.Contains(errors, d => d.NodeKey == "node" && d.TaskKey == "work" &&
            d.Message.Contains("node timeout limit", StringComparison.Ordinal));
        Assert.Contains(errors, d => d.NodeKey == "$post" &&
            d.Message.Contains("10 retries", StringComparison.Ordinal));
    }

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
