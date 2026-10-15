using Wf.Sdk;

namespace Wf.Integration.Tests.Definition;

public sealed class ScenarioADefinitionTests
{
    [Fact]
    public async Task BuildsScenarioAAndResolvesEveryDeclaredTask()
    {
        var calls = new List<string>();
        Action<TaskContext> work = _ => calls.Add("called");
        var workflow = Workflow.Define("payments/release-validation/deploy-check", "1.4.0", w => w
            .Node("build", n => n.Task("restore", work).Task("compile", work))
            .Node("deploy-eu", n => n.DependsOn("build").Task("deploy", work))
            .Node("deploy-us", n => n.DependsOn("build").Task("deploy", work))
            .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us").Parallel(4)
                .Task("api", work).Task("ui", work).Task("perf-probe", work)));

        Assert.Empty(calls);
        Assert.Equal(["build"], workflow.Nodes[1].Dependencies);
        Assert.Equal(["build"], workflow.Nodes[2].Dependencies);
        Assert.Equal(["deploy-eu", "deploy-us"], workflow.Nodes[3].Dependencies);
        foreach (var node in workflow.Nodes)
            foreach (var task in node.Tasks)
                await workflow.Tasks[$"{node.Key}/{task.Key}"](new());
        Assert.Equal(7, calls.Count);
    }
}
