using System.Collections.Concurrent;
using System.Text;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk.Local;

namespace Wf.Sdk.Tests;

public sealed class ReportPublisherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualLocalRunPublishesFromEveryStageAndNodeWithExactSourceAndMetadata()
    {
        var publications = new List<ReportPublication>();
        var reports = new ReportPublisher((report, token) =>
        {
            token.ThrowIfCancellationRequested();
            publications.Add(report);
            return Task.CompletedTask;
        });
        var workflow = Workflow.Define("reports", "1", w => w.Type("test")
            .SystemInit(ctx => Publish(ctx, "init"))
            .PreSetup(ctx => Publish(ctx, "pre"))
            .Node("work", n => n.Task("one", ctx => Publish(ctx, "first"))
                .Task("two", ctx => Publish(ctx, "second")))
            .PostProcess(s => s.Task("summary", ctx => Publish(ctx, "post")))
            .SystemPostProcess(ctx => Publish(ctx, "cleanup")));
        var result = await new LocalEngine(workflow, output: TextWriter.Null, reports: reports).RunAsync(Token);

        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(["$init/run", "$pre/run", "work/one", "work/two", "$post/summary", "$sys-post/run"],
            publications.Select(report => report.SourceKey));
        Assert.Equal(["init", "pre", "first", "second", "post", "cleanup"], publications.Select(report => report.Title));
        Assert.All(publications, report =>
        {
            Assert.Equal("custom-kind.v1", report.Kind);
            Assert.Equal(["review", " exact tag "], report.Tags);
            Assert.Equal(report.Title, Encoding.UTF8.GetString(report.Payload.Span));
        });

        static Task Publish(TaskContext ctx, string title) => ctx.Reports.PublishAsync("custom-kind.v1", title,
            Encoding.UTF8.GetBytes(title), ctx.CancellationToken, ["review", " exact tag "]);
    }

    [Fact]
    public async Task ParallelContextsKeepIndependentSourcesAcrossAwaitAndRepeatedPublishing()
    {
        var publications = new ConcurrentBag<ReportPublication>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var reports = new ReportPublisher(async (report, token) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            publications.Add(report);
        });
        var workflow = Workflow.Define("parallel-reports", "1", w => w.Type("test")
            .Node("left", n => n.Task("work", ctx => Publish(ctx)))
            .Node("right", n => n.Task("work", ctx => Publish(ctx))));
        var result = await new LocalEngine(workflow, output: TextWriter.Null, reports: reports)
            .RunAsync(Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(4, publications.Count);
        Assert.Equal(2, publications.Count(report => report.SourceKey == "left/work"));
        Assert.Equal(2, publications.Count(report => report.SourceKey == "right/work"));
        Assert.All(publications, report => Assert.Equal(report.SourceKey, report.Title));

        static async Task Publish(TaskContext ctx)
        {
            await ctx.Reports.PublishAsync("text", ctx.TaskKey!, ReadOnlyMemory<byte>.Empty, ctx.CancellationToken);
            await Task.Yield();
            await ctx.Reports.PublishAsync("text", ctx.TaskKey!, ReadOnlyMemory<byte>.Empty, ctx.CancellationToken);
        }
    }

    [Fact]
    public async Task PayloadAndTagsAreCopiedBeforeSinkAwaitsWithoutNormalizingAuthorMetadata()
    {
        ReportPublication? received = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = new byte[] { 1, 2, 3 };
        var tags = new[] { "one", "one" };
        var reports = new ReportPublisher(async (report, token) =>
        {
            await release.Task.WaitAsync(token);
            received = report;
        });
        var workflow = Workflow.Define("copy-reports", "1", w => w.Type("test")
            .Node("node", n => n.Task("task", ctx =>
                ctx.Reports.PublishAsync("Unknown Kind", " Exact title ", payload, ctx.CancellationToken, tags))));
        var execution = new TaskRegistry(workflow).ExecuteAsync("node/task", new(Token, reports: reports));
        payload[0] = 9;
        tags[0] = "changed";
        release.SetResult();
        await execution;

        Assert.NotNull(received);
        Assert.Equal("Unknown Kind", received.Kind);
        Assert.Equal(" Exact title ", received.Title);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.Payload.ToArray());
        Assert.Equal(["one", "one"], received.Tags);
    }

    [Theory]
    [InlineData(null, "title")]
    [InlineData("", "title")]
    [InlineData(" \t", "title")]
    [InlineData("text", null)]
    [InlineData("text", "")]
    [InlineData("text", " \t")]
    public async Task BlankKindOrTitleFailsBeforeInvokingSink(string? kind, string? title)
    {
        var calls = 0;
        var reports = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; });
        var workflow = Workflow.Define("invalid-report", "1", w => w.Type("test")
            .Node("node", n => n.Task("task", ctx =>
                ctx.Reports.PublishAsync(kind!, title!, ReadOnlyMemory<byte>.Empty, ctx.CancellationToken))));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await new TaskRegistry(workflow).ExecuteAsync("node/task", new(Token, reports: reports)));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task BlankTagFailsBeforeInvokingSink(string? tag)
    {
        var calls = 0;
        var reports = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; });
        var workflow = Workflow.Define("invalid-tag", "1", w => w.Type("test")
            .Node("node", n => n.Task("task", ctx =>
                ctx.Reports.PublishAsync("text", "title", ReadOnlyMemory<byte>.Empty, ctx.CancellationToken, [tag!]))));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await new TaskRegistry(workflow).ExecuteAsync("node/task", new(Token, reports: reports)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EmptyPayloadAndAbsentTagsAreValidAndSinkReceivesExactCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var calls = 0;
        var reports = new ReportPublisher((report, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Empty(report.Payload.ToArray());
            Assert.Empty(report.Tags);
            calls++;
            return Task.CompletedTask;
        });
        var workflow = Workflow.Define("empty-report", "1", w => w.Type("test")
            .Node("node", n => n.Task("task", ctx =>
                ctx.Reports.PublishAsync("text", "empty", ReadOnlyMemory<byte>.Empty, ctx.CancellationToken))));
        await new TaskRegistry(workflow).ExecuteAsync("node/task", new(cancellation.Token, reports: reports));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationBeforePublishNeverCallsSinkAndDuringPublishPropagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var calls = 0;
        var reports = new ReportPublisher(async (_, token) =>
        {
            calls++;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var workflow = Workflow.Define("cancel-report", "1", w => w.Type("test")
            .Node("node", n => n.Task("task", ctx =>
                ctx.Reports.PublishAsync("text", "title", ReadOnlyMemory<byte>.Empty, ctx.CancellationToken))));
        var registry = new TaskRegistry(workflow);
        var publishing = registry.ExecuteAsync("node/task", new(cancellation.Token, reports: reports));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await registry.ExecuteAsync("node/task", new(cancellation.Token, reports: reports)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SinkFailurePropagatesUnchangedAndDoesNotRetryPublishing()
    {
        var failure = new IOException("report storage unavailable");
        var calls = 0;
        var reports = new ReportPublisher((_, _) => { calls++; return Task.FromException(failure); });
        var workflow = Workflow.Define("failed-report", "1", w => w.Type("test")
            .Node("node", n => n.Task("task", ctx =>
                ctx.Reports.PublishAsync("text", "title", ReadOnlyMemory<byte>.Empty, ctx.CancellationToken))));
        var actual = await Assert.ThrowsAsync<IOException>(async () =>
            await new TaskRegistry(workflow).ExecuteAsync("node/task", new(Token, reports: reports)));
        Assert.Same(failure, actual);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(FailurePolicy.Hard, RunStatus.Failed, ExecutionStatus.Failed)]
    [InlineData(FailurePolicy.Soft, RunStatus.Succeeded, ExecutionStatus.SoftFailed)]
    public async Task ActualLocalRunUsesFailurePolicyAndStillPublishesPostStage(
        FailurePolicy policy, RunStatus expectedRun, ExecutionStatus expectedTask)
    {
        var publications = new List<ReportPublication>();
        var reports = new ReportPublisher((report, _) =>
        {
            if (report.SourceKey == "work/publish")
                throw new IOException("report storage unavailable");
            publications.Add(report);
            return Task.CompletedTask;
        });
        var workflow = Workflow.Define("report-policy", "1", w => w.Type("test")
            .Node("work", n => n.Task("publish", ctx =>
                ctx.Reports.PublishAsync("text", "work", ReadOnlyMemory<byte>.Empty, ctx.CancellationToken), failure: policy))
            .PostProcess(ctx => ctx.Reports.PublishAsync("text", "summary", ReadOnlyMemory<byte>.Empty, ctx.CancellationToken)));
        var result = await new LocalEngine(workflow, output: TextWriter.Null, reports: reports).RunAsync(Token);
        Assert.Equal(expectedRun, result.Snapshot.Status);
        Assert.Equal(expectedTask, result.Snapshot.Tasks["work/publish"]);
        Assert.Equal("$post/run", Assert.Single(publications).SourceKey);
    }

    [Fact]
    public async Task UnboundPublisherFailsExplicitlyAndCannotInventASource()
    {
        Assert.Throws<ArgumentNullException>(() => new ReportPublisher(null!));
        var calls = 0;
        var reports = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reports.PublishAsync("text", "title", ReadOnlyMemory<byte>.Empty, Token));
        Assert.Contains("executing task context", error.Message);
        Assert.Equal(0, calls);
    }
}
