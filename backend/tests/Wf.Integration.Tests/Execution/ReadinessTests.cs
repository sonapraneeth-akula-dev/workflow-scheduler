using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Scheduling;
using Wf.Engine.Core.Stages;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed class ReadinessTests
{
    [Fact]
    public void SdkConditionsAndStageEligibilityDriveTheSameCoreReadinessRules()
    {
        var workflow = Workflow.Define("readiness", "1", w => w.Type("test")
            .SystemInit(_ => { })
            .Node("source", _ => { })
            .Node("skip", n => n.DependsOn("source"))
            .Node("cleanup", n => n.DependsOn("source", DependencyCondition.Always))
            .PostProcess(_ => { }));
        var states = workflow.Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        states["$init"] = ExecutionStatus.Succeeded;
        states["source"] = ExecutionStatus.Failed;
        var plan = StagePlanner.Plan(workflow.Nodes, states, RunStatus.Running);
        var changes = ReadinessEvaluator.Evaluate(workflow.Nodes, states, plan.EligibleNodes);
        Assert.Contains(new NodeStateChange("skip", ExecutionStatus.Skipped), changes);
        Assert.Contains(new NodeStateChange("cleanup", ExecutionStatus.Ready), changes);
        Assert.DoesNotContain(changes, c => c.Node == "$post");
    }
}
