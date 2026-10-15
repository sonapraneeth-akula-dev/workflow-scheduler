using Wf.Contracts;
using Wf.Engine.Core.Transitions;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed class FailurePolicyTests
{
    [Fact]
    public async Task SoftFunctionFailureDoesNotCancelTheNextDeclaredTask()
    {
        var continued = false;
        var workflow = Workflow.Define("soft", "1", w => w.Type("test").Node("node", n => n
            .Task("fail", _ => Task.FromException(new InvalidOperationException("expected")), failure: Failure.Soft)
            .Task("next", _ => { continued = true; })));
        var node = workflow.Nodes[0];
        var states = node.Tasks.ToDictionary(t => t.Key, _ => ExecutionStatus.Pending);
        states["fail"] = ExecutionStatus.Running;
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.Tasks["node/fail"](new()));
        foreach (var change in FailurePropagation.Plan(node, "fail", states).Changes)
            states[change.Task] = change.Status;
        await workflow.Tasks["node/next"](new());
        states["next"] = ExecutionStatus.Succeeded;
        Assert.True(continued);
        Assert.Equal(ExecutionStatus.SoftFailed, FailurePropagation.ResolveNode(node, states));
    }
}
