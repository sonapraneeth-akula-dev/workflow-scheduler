using Wf.Contracts;
using Wf.Engine.Core.Stages;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed class StagePlannerTests
{
    [Fact]
    public async Task AuthoringAndCorePlannerShareStageNodesAndRunCleanupAfterFailure()
    {
        var calls = new List<string>();
        var workflow = Workflow.Define("stages", "1", w => w.Type("test")
            .SystemPostProcess(_ => { calls.Add("cleanup"); })
            .PostProcess(_ => { calls.Add("report"); })
            .Node("work", n => n.Task("fail", _ => Task.FromException(new InvalidOperationException("failed"))))
            .SystemInit(_ => { calls.Add("init"); }));
        var states = workflow.Nodes.ToDictionary(n => n.Key, _ => ExecutionStatus.Pending);
        while (true)
        {
            var plan = StagePlanner.Plan(workflow.Nodes, states, RunStatus.Running);
            foreach (var change in plan.Changes)
                states[change.Node] = change.Status;
            if (plan.Complete)
                break;
            foreach (var key in plan.EligibleNodes)
            {
                var node = workflow.Nodes.Single(n => n.Key == key);
                states[key] = ExecutionStatus.Succeeded;
                try
                {
                    foreach (var task in node.Tasks)
                        await workflow.Tasks[$"{key}/{task.Key}"](new());
                }
                catch (InvalidOperationException error) when (error.Message == "failed")
                {
                    states[key] = ExecutionStatus.Failed;
                }
            }
        }
        Assert.Equal(["init", "report", "cleanup"], calls);
        Assert.Equal(ExecutionStatus.Failed, states["work"]);
    }
}
