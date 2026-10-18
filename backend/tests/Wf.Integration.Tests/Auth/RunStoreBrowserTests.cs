using System.Diagnostics;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Live;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task RunStore_browser_fills_dropped_frames_and_reloads_after_actual_409_without_polling()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_LIVE_BROWSER") == "true",
            "Set WF_LIVE_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "run-store-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var run = new RunState(Guid.CreateVersion7(), [new NodeDefinition("node", [], [new TaskDefinition("task")])],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), _ => { });
        await new RunStore(database.DataSource, NullLogger<RunStore>.Instance).CreateAsync(run,
            seed.Type + "/run-store", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1),
            TestContext.Current.CancellationToken);
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        using var writer = new SemaphoreSlim(1);
        await using var server = await Server.Start(database.DataSource, live: true, configure: app =>
        {
            app.MapPost("/api/test/run-store/append/{count:int}", async (int count, HttpContext context) =>
            {
                if (count is < 1 or > 6001) return Results.BadRequest();
                await writer.WaitAsync(context.RequestAborted);
                try
                {
                    var preview = RunState.Restore(run.Snapshot, [], _ => { });
                    var changes = new List<RunEvent>(count);
                    for (var index = 0; index < count; index++)
                    {
                        if (preview.Snapshot.LastSequence == 0)
                            changes.Add(preview.Append(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow));
                        else
                        {
                            var next = preview.Snapshot.Tasks["node/task"] switch
                            {
                                ExecutionStatus.Pending => ExecutionStatus.Ready,
                                ExecutionStatus.Ready or ExecutionStatus.Retrying => ExecutionStatus.Dispatched,
                                ExecutionStatus.Dispatched => ExecutionStatus.Running,
                                ExecutionStatus.Running => ExecutionStatus.Retrying,
                                _ => throw new InvalidOperationException("Unexpected browser fixture task state.")
                            };
                            changes.Add(preview.Append(EventEntity.Task, "node/task", (int)next, DateTimeOffset.UtcNow));
                        }
                    }
                    foreach (var batch in changes.Chunk(128))
                    {
                        await appender.AppendAsync(batch, context.RequestAborted);
                        foreach (var change in batch) run.Apply(change);
                    }
                    return Results.Ok(new { lastSeq = run.Snapshot.LastSequence });
                }
                finally { writer.Release(); }
            }).RequireAuthorization();
            app.MapPost("/api/test/run-store/resync", async (IHubContext<LiveHub> hub, HttpContext context) =>
            {
                await hub.Clients.All.SendAsync("Resync", new LiveResync("run", run.Snapshot.RunId.ToString("D")),
                    context.RequestAborted);
                return Results.NoContent();
            }).RequireAuthorization();
        });
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "run-store-browser.ts"));
        start.Environment["WF_LIVE_API"] = server.Api;
        start.Environment["WF_LIVE_TOKEN"] = server.Token(group: group);
        start.Environment["WF_LIVE_RUN"] = run.Snapshot.RunId.ToString("D");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Run store browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }
}
