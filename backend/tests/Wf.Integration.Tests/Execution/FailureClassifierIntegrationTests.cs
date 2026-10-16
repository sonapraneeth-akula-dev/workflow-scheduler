using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Integration.Tests.Execution;

public sealed class FailureClassifierIntegrationTests
{
    [Theory]
    [InlineData(FailureCategory.TaskError)]
    [InlineData(FailureCategory.Timeout)]
    [InlineData(FailureCategory.Aborted)]
    [InlineData(FailureCategory.AgentLost)]
    [InlineData(FailureCategory.ResourceLimit)]
    [InlineData(FailureCategory.ValidationError)]
    [InlineData(FailureCategory.Placement)]
    [InlineData(FailureCategory.PlatformError)]
    public async Task ScriptedTypedSignalsReachActualLocalAttemptsWithoutLosingPermanentFailureMetadata(FailureCategory category)
    {
        var workflow = Workflow.Define("failure-classifier", "1", w => w.Type("test").Node("node", n => n
            .Task("fail", _ => throw new TaskFailureException("Scripted explicit signal; not external fault injection.",
                retryable: false, category: category), p => p.Retry(3, Backoff.Fixed(TimeSpan.Zero)))));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);
        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(category, attempt.Failure);
        Assert.Equal(category, result.Snapshot.Metrics!.Tasks["node/fail"].FailureCategory);
        Assert.Equal(category, Assert.Single(result.Snapshot.Metrics.Tasks["node/fail"].Attempts).FailureCategory);
        Assert.Equal(category, result.Snapshot.Metrics.Nodes["node"].FailureCategory);
        Assert.Equal(category, result.Snapshot.Metrics.Run.FailureCategory);
        Assert.False(Assert.IsType<TaskFailureException>(attempt.Error).Retryable);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task SelfCancellationWithoutEngineCancellationIsAnAuthorErrorAndCanUseAuthorRetry()
    {
        var calls = 0;
        var workflow = Workflow.Define("failure-classifier-self-cancel", "1", w => w.Type("test").Node("node", n => n
            .Task("work", _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new OperationCanceledException("Timeout Aborted AgentLost");
            }, p => p.Retry(1, Backoff.Fixed(TimeSpan.Zero)).NoAutoRetry())));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Attempts.Length);
        Assert.Equal(FailureCategory.TaskError, result.Attempts[0].Failure);
        Assert.Equal(ExecutionStatus.Failed, result.Attempts[0].Status);
        Assert.Null(result.Attempts[1].Failure);
        Assert.Equal(FailureCategory.TaskError, result.Snapshot.Metrics!.Tasks["node/work"].Attempts[0].FailureCategory);
        Assert.Null(result.Snapshot.Metrics.Tasks["node/work"].Attempts[1].FailureCategory);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualDeadlinePreservesTimedOutStateWithoutMaskingNormalOrAuthorError(bool explicitAuthorError)
    {
        var workflow = Workflow.Define("failure-classifier-timeout-race", "1", w => w.Type("test").Node("node", n => n
            .Task("work", async ctx =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken); }
                catch (OperationCanceledException)
                {
                    if (explicitAuthorError)
                        throw new TaskFailureException("author error", category: FailureCategory.ValidationError);
                    throw new InvalidOperationException("normal task error");
                }
            }, p => p.Timeout(TimeSpan.FromMilliseconds(60)))));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(explicitAuthorError ? FailureCategory.ValidationError : FailureCategory.TaskError, attempt.Failure);
        Assert.Equal(attempt.Failure, result.Snapshot.Metrics!.Tasks["node/work"].FailureCategory);
        Assert.Equal(ExecutionStatus.TimedOut, attempt.Status);
        Assert.Equal(ExecutionStatus.TimedOut, result.Snapshot.Tasks["node/work"]);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
    }

    [Fact]
    public async Task TypedTimeoutClassificationDoesNotChangeAnUncancelledAttemptLifecycle()
    {
        var workflow = Workflow.Define("failure-classifier-task-timeout", "1", w => w.Type("test").Node("node", n => n
            .Task("work", _ => throw new TimeoutException("typed task timeout"))));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);
        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(FailureCategory.Timeout, attempt.Failure);
        Assert.Equal(FailureCategory.Timeout, result.Snapshot.Metrics!.Tasks["node/work"].FailureCategory);
        Assert.Equal(ExecutionStatus.Failed, attempt.Status);
        Assert.Equal(ExecutionStatus.Failed, result.Snapshot.Tasks["node/work"]);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalOrAuthorErrorAfterActualAbortDoesNotBecomeAnAbortFailure(bool explicitAuthorError)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = Workflow.Define("failure-classifier-abort-race", "1", w => w.Type("test").Node("node", n => n
            .Task("work", async ctx =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken); }
                catch (OperationCanceledException)
                {
                    if (explicitAuthorError)
                        throw new TaskFailureException("author error", category: FailureCategory.ValidationError);
                    throw new InvalidOperationException("normal task error");
                }
            })));
        var engine = new LocalEngine(workflow, output: TextWriter.Null);
        var execution = engine.RunAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await engine.AbortAsync(new("test", "Test user", DateTimeOffset.UtcNow, "Requested abort", AbortSource.Local));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(explicitAuthorError ? FailureCategory.ValidationError : FailureCategory.TaskError, attempt.Failure);
        Assert.Equal(attempt.Failure, result.Snapshot.Metrics!.Tasks["node/work"].FailureCategory);
        Assert.Equal(ExecutionStatus.Cancelled, attempt.Status);
        Assert.Equal(RunStatus.Aborted, result.Snapshot.Status);
    }
}
