using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wf.Contracts;
using Wf.Engine.Core;
using Wf.Engine.Core.Rerun;
using Wf.Engine.Core.Scheduling;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Integration.Tests.Execution;

public sealed class RerunPlannerIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_failed_SDK_workflow_supplies_original_variables_and_outputs_to_the_shared_rerun_plan(bool selectBuild)
    {
        using var output = new CompletionOutput();
        var buildCalls = 0;
        var euCalls = 0;
        var workflow = Workflow.Define("demo/release/scenario-a-rerun", "1.4.0", w => w.Type("release")
            .Variables(variables => variables.String("environment", "dev"))
            .Node("build", node => node.Task("compile", context =>
            {
                Interlocked.Increment(ref buildCalls);
                context.Outputs["artifact"] = "package-v1.4.0";
                context.Outputs["environment"] = context.Var<string>("environment");
            }))
            .Node("deploy-eu", node => node.DependsOn("build").Task("deploy", context =>
            {
                Interlocked.Increment(ref euCalls);
                context.Outputs["endpoint"] = "https://eu.invalid/v1.4.0";
            }))
            .Node("deploy-us", node => node.DependsOn("build").Task("deploy", async context =>
            {
                await output.EuSucceeded.Task.WaitAsync(context.CancellationToken);
                throw new InvalidOperationException("Original US deployment failed.");
            }))
            .Node("smoke", node => node.DependsOn("deploy-eu", "deploy-us").Task("check", _ =>
                throw new InvalidOperationException("Skipped smoke must not execute."))));
        var result = await new LocalEngine(workflow,
            variables: ImmutableDictionary<string, JsonElement>.Empty.Add("environment", JsonSerializer.SerializeToElement("ppe")),
            output: output).RunAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Nodes["build"]);
        Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Nodes["deploy-eu"]);
        Assert.Equal(ExecutionStatus.Failed, result.Snapshot.Nodes["deploy-us"]);
        Assert.Equal(ExecutionStatus.Skipped, result.Snapshot.Nodes["smoke"]);
        var versionId = Guid.CreateVersion7();
        var persistedOutputs = result.Outputs.ToImmutableDictionary(pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal);
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), new(versionId, result.Snapshot, persistedOutputs),
            workflow.Nodes, selectBuild ? ["build"] : null);
        Assert.Equal(versionId, plan.VersionId);
        Assert.Equal(result.Snapshot.RunId, plan.ParentRunId);
        Assert.Equal("ppe", plan.Snapshot.Variables["environment"].GetString());
        Assert.Equal(1, buildCalls);
        Assert.Equal(1, euCalls);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes["deploy-us"]);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes["smoke"]);
        if (selectBuild)
        {
            Assert.Empty(plan.ReusedNodes);
            Assert.All(plan.Snapshot.Nodes.Values, state => Assert.Equal(ExecutionStatus.Pending, state));
        }
        else
        {
            Assert.Equal("package-v1.4.0", plan.ReusedNodes["build"].Outputs.GetProperty("artifact").GetString());
            Assert.Equal("ppe", plan.ReusedNodes["build"].Outputs.GetProperty("environment").GetString());
            Assert.Equal("https://eu.invalid/v1.4.0", plan.ReusedNodes["deploy-eu"].Outputs.GetProperty("endpoint").GetString());
            var restored = RunState.Restore(plan.Snapshot, [], Assert.Fail);
            Assert.Equal(["deploy-us"], ReadinessEvaluator.Evaluate(workflow.Nodes, restored.Snapshot.Nodes).Select(change => change.Node));
            Assert.All(plan.ReusedNodes.Values, node => Assert.Equal(result.Snapshot.RunId, node.ReusedFromRunId));
        }
        Assert.Equal(1, buildCalls);
        Assert.Equal(1, euCalls);
    }

    private sealed class CompletionOutput : TextWriter
    {
        public TaskCompletionSource EuSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            if (value == "Node deploy-eu: Succeeded") EuSucceeded.TrySetResult();
        }
    }
}
