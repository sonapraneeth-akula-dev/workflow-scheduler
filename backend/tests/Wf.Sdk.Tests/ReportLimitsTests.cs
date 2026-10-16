using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk.Local;

namespace Wf.Sdk.Tests;

public sealed class ReportLimitsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TenReportsSucceedAndEleventhFailsBeforeSinkWithClearNonretryableError()
    {
        var calls = 0;
        var publisher = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; });
        var registry = Registry(ctx => Publish(ctx));
        for (var index = 0; index < 10; index++)
            await registry.ExecuteAsync("work/publish", new(Token, reports: publisher));
        var error = await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            registry.ExecuteAsync("work/publish", new(Token, reports: publisher)));
        Assert.Contains("10 reports", error.Message);
        Assert.False(error.Retryable);
        Assert.Equal(10, calls);
    }

    [Fact]
    public async Task ExactlyTenMillionBytesSucceedAndOneExtraByteFailsBeforeSink()
    {
        var calls = 0;
        var publisher = new ReportPublisher((report, _) =>
        {
            Assert.Equal(10_000_000, report.Payload.Length);
            calls++;
            return Task.CompletedTask;
        });
        var payload = new byte[10_000_001];
        var registry = Registry(ctx => ctx.Reports.PublishAsync("text", "size", payload, ctx.CancellationToken));
        var error = await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            registry.ExecuteAsync("work/publish", new(Token, reports: publisher)));
        Assert.Contains("10000001 bytes", error.Message);
        Assert.Contains("10000000 bytes", error.Message);
        Assert.False(error.Retryable);
        var exact = Registry(ctx => ctx.Reports.PublishAsync("text", "size",
            payload.AsMemory(0, 10_000_000), ctx.CancellationToken));
        await exact.ExecuteAsync("work/publish", new(Token, reports: publisher));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ValidatedTypeOverridesControlCountAndBytesIndependently()
    {
        using var json = JsonDocument.Parse("""{"artifacts":{"maxBytes":99},"reports":{"maxCount":2,"maxBytes":3}}""");
        var limits = ReportLimits.FromTypeLimits(json.RootElement);
        var calls = 0;
        var publisher = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; }, limits);
        var tooLarge = Registry(ctx => ctx.Reports.PublishAsync("json", "large", new byte[4], ctx.CancellationToken));
        await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            tooLarge.ExecuteAsync("work/publish", new(Token, reports: publisher)));
        var valid = Registry(ctx => ctx.Reports.PublishAsync("unknown", "exact", new byte[3], ctx.CancellationToken));
        await valid.ExecuteAsync("work/publish", new(Token, reports: publisher));
        await valid.ExecuteAsync("work/publish", new(Token, reports: publisher));
        await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            valid.ExecuteAsync("work/publish", new(Token, reports: publisher)));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("null", 10, 10_000_000)]
    [InlineData("{}", 10, 10_000_000)]
    [InlineData("""{"other":1}""", 10, 10_000_000)]
    [InlineData("""{"reports":{}}""", 10, 10_000_000)]
    [InlineData("""{"reports":{"maxCount":12}}""", 12, 10_000_000)]
    [InlineData("""{"reports":{"maxBytes":20000000}}""", 10, 20_000_000)]
    public void MissingOverrideValuesUseDocumentedDefaults(string json, int count, long bytes)
    {
        using var document = JsonDocument.Parse(json);
        var limits = ReportLimits.FromTypeLimits(document.RootElement);
        Assert.Equal(count, limits.MaxCount);
        Assert.Equal(bytes, limits.MaxBytes);
        Assert.Equal(new ReportLimits(), ReportLimits.FromTypeLimits(null));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("""{"reports":null}""")]
    [InlineData("""{"reports":[]}""")]
    [InlineData("""{"reports":{"maxCount":0}}""")]
    [InlineData("""{"reports":{"maxCount":-1}}""")]
    [InlineData("""{"reports":{"maxCount":"2"}}""")]
    [InlineData("""{"reports":{"maxCount":1.5}}""")]
    [InlineData("""{"reports":{"maxCount":2147483648}}""")]
    [InlineData("""{"reports":{"maxBytes":0}}""")]
    [InlineData("""{"reports":{"maxBytes":-1}}""")]
    [InlineData("""{"reports":{"maxBytes":1.5}}""")]
    [InlineData("""{"reports":{"maxBytes":"3"}}""")]
    [InlineData("""{"reports":{"maxBytes":9223372036854775808}}""")]
    [InlineData("""{"reports":{"maxCount":2,"maxCount":3}}""")]
    [InlineData("""{"reports":{},"reports":{}}""")]
    [InlineData("""{"reports":{"maxByte":3}}""")]
    public void InvalidOrAmbiguousTypeOverridesFailExplicitly(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => ReportLimits.FromTypeLimits(document.RootElement));
    }

    [Fact]
    public void InvalidProgrammaticLimitsFailAtConfigurationBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportLimits(maxCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportLimits(maxCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportLimits(maxBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportLimits(maxBytes: -1));
        Assert.Throws<ArgumentNullException>(() => new ReportPublisher((_, _) => Task.CompletedTask, null!));
    }

    [Fact]
    public async Task ParallelPublishingReservesInFlightSlotsWithoutExceedingLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var publisher = new ReportPublisher(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 10)
                entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        var registry = Registry(Publish);
        var attempts = Enumerable.Range(0, 32).Select(_ => Record.ExceptionAsync(() =>
            registry.ExecuteAsync("work/publish", new(Token, reports: publisher))).AsTask()).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(10, Volatile.Read(ref calls));
        release.SetResult();
        var errors = await Task.WhenAll(attempts);
        Assert.Equal(10, errors.Count(error => error is null));
        Assert.Equal(22, errors.Count(error => error is ReportLimitExceededException));
        Assert.Equal(10, calls);
    }

    [Fact]
    public async Task FailedSinkReleasesReservationWithoutHidingItsOriginalError()
    {
        var failure = new IOException("storage rejected report");
        var calls = 0;
        var publisher = new ReportPublisher((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw failure;
            return Task.CompletedTask;
        }, new(maxCount: 1));
        var registry = Registry(Publish);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            registry.ExecuteAsync("work/publish", new(Token, reports: publisher))));
        await registry.ExecuteAsync("work/publish", new(Token, reports: publisher));
        await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            registry.ExecuteAsync("work/publish", new(Token, reports: publisher)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelledSinkReleasesReservationForTheNextPublish()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var publisher = new ReportPublisher(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, new(maxCount: 1));
        var registry = Registry(Publish);
        var first = registry.ExecuteAsync("work/publish", new(cancellation.Token, reports: publisher));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await registry.ExecuteAsync("work/publish", new(Token, reports: publisher));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ActualLocalRunSharesQuotaAcrossStagesAndTasksButResetsItForAnotherRun()
    {
        var calls = 0;
        var publisher = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; }, new(maxCount: 2));
        var workflow = Workflow.Define("stage-limits", "1", w => w.Type("test")
            .SystemInit(Publish)
            .Node("work", n => n.Task("publish", Publish))
            .PostProcess(Publish));
        for (var run = 0; run < 2; run++)
        {
            var result = await new LocalEngine(workflow, output: TextWriter.Null, reports: publisher).RunAsync(Token);
            Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
            Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Tasks["work/publish"]);
            Assert.Equal(ExecutionStatus.Failed, result.Snapshot.Tasks["$post/run"]);
        }
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(FailurePolicy.Hard, RunStatus.Failed, ExecutionStatus.Failed)]
    [InlineData(FailurePolicy.Soft, RunStatus.Succeeded, ExecutionStatus.SoftFailed)]
    public async Task ActualLocalRunAppliesFailurePolicyWithoutRetryingImpossibleSize(
        FailurePolicy policy, RunStatus expectedRun, ExecutionStatus expectedTask)
    {
        var calls = 0;
        var executions = 0;
        var publisher = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; }, new(maxBytes: 1));
        var workflow = Workflow.Define("limit-policy", "1", w => w.Type("test")
            .Node("work", n => n.Task("publish", ctx =>
            {
                executions++;
                return ctx.Reports.PublishAsync("text", "large", new byte[2], ctx.CancellationToken);
            }, p => p.Retry(2, Backoff.Fixed(TimeSpan.Zero)), failure: policy))
            .PostProcess(Publish));
        var result = await new LocalEngine(workflow, output: TextWriter.Null, reports: publisher).RunAsync(Token);
        Assert.Equal(expectedRun, result.Snapshot.Status);
        Assert.Equal(expectedTask, result.Snapshot.Tasks["work/publish"]);
        Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Tasks["$post/run"]);
        Assert.Equal(1, executions);
        Assert.Equal(1, calls);
    }

    private static TaskRegistry Registry(Func<TaskContext, Task> publish) =>
        new(Workflow.Define("report-limits", "1", w => w.Type("test")
            .Node("work", n => n.Task("publish", publish))));

    private static Task Publish(TaskContext ctx) =>
        ctx.Reports.PublishAsync("text", "report", ReadOnlyMemory<byte>.Empty, ctx.CancellationToken);
}
