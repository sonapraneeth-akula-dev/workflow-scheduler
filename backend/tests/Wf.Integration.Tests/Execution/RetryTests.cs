using Wf.Contracts.Definition;
using Wf.Engine.Core.Retries;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed class RetryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommandExitCodeRetryabilityReachesCoreEvaluator(bool retryable)
    {
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows() ? ["/d", "/s", "/c", "exit /b 3"] : ["-c", "exit 3"];
        var workflow = Workflow.Define("retry", "1", w => w.Type("test").Defaults(d => d.Retry(1))
            .Node("node", n => n.Task("command", Tasks.Command(shell, arguments, retryableExitCodes: retryable ? [3] : []))));
        var error = await Assert.ThrowsAsync<CommandTaskException>(() =>
            new TaskRegistry(workflow).ExecuteAsync("node/command", new(TestContext.Current.CancellationToken)));
        var now = DateTimeOffset.UtcNow;
        var context = new RetryContext(FailureCategory.TaskError, 0, 0, now, now.AddHours(1));
        var decision = RetryPolicyEvaluator.Evaluate(workflow.Nodes[0].Tasks[0].Policy, context, 0.5, error);
        Assert.Equal(retryable, decision.Retry);
    }
}
