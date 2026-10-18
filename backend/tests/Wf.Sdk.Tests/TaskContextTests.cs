using System.Text;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class TaskContextTests
{
    [Fact]
    public async Task RegistrySuppliesStableIdentityVariablesOutputsLoggingAndPublishers()
    {
        var logs = new List<string>();
        var reports = new Reports();
        var artifacts = new Artifacts();
        var workflow = Workflow.Define("context", "1", w => w.Type("test")
            .Node("node", n => n.Task("work", async ctx =>
            {
                Assert.Equal("node/work", ctx.TaskKey);
                Assert.Equal("dev", ctx.Var<string>("env"));
                ctx.Outputs["result"] = 42;
                ctx.Log("working");
                await ctx.Reports.PublishAsync("text", "Summary", Encoding.UTF8.GetBytes("done"), ctx.CancellationToken);
                using var stream = new MemoryStream([1, 2, 3]);
                Assert.Equal(new Uri("file:///artifact.bin"),
                    await ctx.Artifacts.PublishAsync("artifact.bin", stream, "application/octet-stream", ctx.CancellationToken));
            })));
        var outputs = new NodeOutputs("node");
        var context = new TaskContext(TestContext.Current.CancellationToken,
            variables: new Dictionary<string, object> { ["env"] = "dev" }, outputs: outputs,
            logger: logs.Add, reports: reports, artifacts: artifacts);
        await new TaskRegistry(workflow).ExecuteAsync("node/work", context);
        Assert.Null(context.TaskKey);
        Assert.Equal(42, outputs.Get<int>("result"));
        Assert.Equal(["working"], logs);
        Assert.Equal(["done"], reports.Payloads);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(artifacts.Payloads));
    }

    [Fact]
    public async Task CancellationReachesRunningFunctionAndPreventsStartingNewFunctions()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var workflow = Workflow.Define("cancel", "1", w => w.Type("test").Node("node", n => n.Task("wait", async ctx =>
        {
            using var registration = ctx.CancellationToken.Register(() => observed = true);
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
        })));
        var registry = new TaskRegistry(workflow);
        var task = registry.ExecuteAsync("node/wait", new(cancellation.Token));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(observed);
        await Assert.ThrowsAsync<OperationCanceledException>(() => registry.ExecuteAsync("node/wait", new(cancellation.Token)));
    }

    [Fact]
    public async Task MissingDelegatesOrPublishersFailExplicitly()
    {
        var workflow = Workflow.Define("empty", "1", w => w.Type("test"));
        Assert.Contains("missing/task", (await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new TaskRegistry(workflow).ExecuteAsync("missing/task", new()))).Message);
        Assert.Throws<NotSupportedException>(() => new TaskContext().Reports);
        Assert.Throws<NotSupportedException>(() => new TaskContext().Artifacts);
        Assert.Throws<ArgumentNullException>(() => new TaskContext().Log(null!));
    }

    private sealed class Reports : IReports
    {
        public List<string> Payloads { get; } = [];
        public Task PublishAsync(string kind, string title, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken, IReadOnlyList<string>? tags = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Payloads.Add(Encoding.UTF8.GetString(payload.Span));
            return Task.CompletedTask;
        }
    }

    private sealed class Artifacts : IArtifacts
    {
        public List<byte[]> Payloads { get; } = [];
        public async Task<Uri> PublishAsync(string name, Stream content, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Payloads.Add(buffer.ToArray());
            return new("file:///artifact.bin");
        }
    }
}
