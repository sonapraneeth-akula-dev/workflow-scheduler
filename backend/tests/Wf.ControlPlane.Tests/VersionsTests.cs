using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class VersionsTests
{
    [Fact]
    public void Task_plan_is_an_explicit_safe_projection_with_dependencies_policies_and_declared_actions()
    {
        var placement = new PlacementPolicy(new(pool: new(variable: "pool")));
        var policy = new ExecutionPolicy(TimeSpan.FromSeconds(12), new(2,
            new(BackoffKind.Exponential, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8)),
            [FailureCategory.AgentLost]), false);
        var task = new TaskDefinition("execute", policy: policy, placement: placement,
            action: new(TaskActionKind.SdkFunction, "Declared.Workflow.Execute"));
        var manifest = new WorkflowManifest("1", "team/type/plan", "1.2.3",
            new("type", ImmutableSortedDictionary<string, string>.Empty.Add("private", "metadata-secret")),
            [new("password", VariableType.String, @default: JsonSerializer.SerializeToElement("default-secret"))],
            ["$init"], [new("work", [new("$init", DependencyCondition.Always)], [task],
                inputs: [new("$init", "private-input")], timeout: TimeSpan.FromMinutes(1), placement: placement)],
            TimeSpan.FromHours(1), placement);
        var plan = WorkflowVersionPlan.From(Guid.CreateVersion7(), manifest);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan, VersionsJsonContext.Default.WorkflowVersionPlan));
        var root = json.RootElement;
        Assert.Equal(new[] { "nodes", "placement", "runTimeout", "stages", "version", "versionId", "workflowId" },
            root.EnumerateObject().Select(property => property.Name).Order());
        var node = Assert.Single(plan.Nodes);
        Assert.Equal(new("$init", DependencyCondition.Always), Assert.Single(node.Dependencies));
        Assert.Equal(TimeSpan.FromMinutes(1), node.Timeout);
        Assert.Equal(policy.AttemptTimeout, Assert.Single(node.Tasks).Policy.AttemptTimeout);
        Assert.Equal(policy.Retry.Retries, node.Tasks[0].Policy.Retry.Retries);
        Assert.Equal(policy.Retry.On, node.Tasks[0].Policy.Retry.On);
        Assert.False(node.Tasks[0].Policy.AutoRetry);
        Assert.Equal("pool", node.Tasks[0].Placement!.Selector!.Pool!.Variable);
        Assert.Equal("SdkFunction", root.GetProperty("nodes")[0].GetProperty("tasks")[0].GetProperty("action").GetProperty("kind").GetString());
        foreach (var value in new[] { "metadata-secret", "default-secret", "private-input", "\"variables\"", "\"inputs\"" })
            Assert.DoesNotContain(value, root.GetRawText(), StringComparison.Ordinal);
    }
}
