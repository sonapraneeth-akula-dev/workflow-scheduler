using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Engine.Core;
using Wf.Engine.Core.Abort;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed class AbortTests
{
    [Fact]
    public async Task CommittedAbortCancelsFunctionAndReachesCleanupThroughRegistry()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AbortRecord? seen = null;
        var workflow = Workflow.Define("abort", "1", w => w.Type("test").Node("node", n => n.Task("wait", async ctx =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
        })).PostProcess(ctx => { seen = ctx.Abort; }));
        var now = DateTimeOffset.UtcNow;
        var run = new RunState(Guid.CreateVersion7(), workflow.Nodes, workflow.Metadata, Assert.Fail);
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, now);
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
        {
            run.Append(EventEntity.Node, "node", (int)state, now);
            run.Append(EventEntity.Task, "node/wait", (int)state, now);
        }
        var registry = new TaskRegistry(workflow);
        var function = registry.ExecuteAsync("node/wait", new(cancellation.Token));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var record = new AbortRecord("operator", "Operator", now, "Integration abort", AbortSource.Local);
        var request = AbortHandler.Request(run.Snapshot, record);
        run.Apply(run.CreateEvent(EventEntity.Run, null, (int)request.Status!, now, request.Record));
        var plan = AbortHandler.Plan(workflow.Nodes, run.Snapshot);
        Assert.Equal(["node/wait"], plan.CancelRunning);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => function);
        run.Append(EventEntity.Task, "node/wait", (int)ExecutionStatus.Cancelled, now);
        foreach (var node in AbortHandler.Plan(workflow.Nodes, run.Snapshot).Nodes)
            run.Append(EventEntity.Node, node.Node, (int)node.Status, now);
        var post = workflow.Nodes.Single(n => n.Key == "$post");
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
        {
            run.Append(EventEntity.Node, post.Key, (int)state, now);
            run.Append(EventEntity.Task, $"{post.Key}/{post.Tasks[0].Key}", (int)state, now);
        }
        await registry.ExecuteAsync($"{post.Key}/{post.Tasks[0].Key}", new(abort: run.Snapshot.Abort));
        run.Append(EventEntity.Task, $"{post.Key}/{post.Tasks[0].Key}", (int)ExecutionStatus.Succeeded, now.AddSeconds(1));
        run.Append(EventEntity.Node, post.Key, (int)ExecutionStatus.Succeeded, now.AddSeconds(1));
        var complete = AbortHandler.Plan(workflow.Nodes, run.Snapshot);
        run.Append(EventEntity.Run, null, (int)complete.Status!, now.AddSeconds(1));
        Assert.Equal(record, seen);
        Assert.Equal(RunStatus.Aborted, run.Snapshot.Status);
        Assert.Equal(now.AddSeconds(1), run.Snapshot.EndedAt);
    }
}
