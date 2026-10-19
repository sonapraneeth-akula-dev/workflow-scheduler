using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.Engine.Core;
using Wf.Sdk;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Run_graph_Chrome_trace_measures_200_nodes_during_100_committed_events_per_second()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_GRAPH_BROWSER") == "true",
            "Set WF_GRAPH_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and Chromium.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "run-graph-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflowId = seed.Type + "/graph";
        await CatalogWorkflow(workflowId);
        var definition = Workflow.Define(workflowId, "1.0.0", builder =>
        {
            builder.Type("release");
            for (var index = 0; index < 200; index++)
            {
                var current = index;
                builder.Node($"node-{current:D3}", node =>
                {
                    if (current >= 10)
                    {
                        node.DependsOn($"node-{current - 10:D3}");
                        if (current % 10 > 0) node.DependsOn($"node-{current - 11:D3}");
                    }
                    if (current == 197) node.NoAutoRetry().Retry(2);
                    if (current == 198) node.NoAutoRetry();
                    if (current == 199) node.OnFailure(FailurePolicy.Soft);
                    node.Task("work", DeclaredPlanTask);
                    if (current == 199)
                        node.Task("notify", DeclaredPlanTask, policy => policy.NoAutoRetry().Retry(2),
                            FailurePolicy.Soft);
                });
            }
        });
        Guid versionId;
        await using (var version = database.DataSource.CreateCommand("""
            UPDATE workflow_versions SET manifest=$2 WHERE workflow_id=$1 RETURNING id
            """))
        {
            version.Parameters.AddWithValue(workflowId);
            version.Parameters.AddWithValue(NpgsqlDbType.Jsonb, ManifestWriter.Write(definition));
            versionId = Assert.IsType<Guid>(await version.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        var run = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, _ => { });
        await new RunStore(database.DataSource, NullLogger<RunStore>.Instance).CreateAsync(run,
            workflowId, versionId, DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        var initial = RunState.Restore(run.Snapshot, [], _ => { });
        var setup = new List<RunEvent> { initial.Append(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow) };
        foreach (var node in definition.Nodes)
            foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                setup.Add(initial.Append(EventEntity.Node, node.Key, (int)status, DateTimeOffset.UtcNow));
        foreach (var batch in setup.Chunk(128))
        {
            await appender.AppendAsync(batch, TestContext.Current.CancellationToken);
            foreach (var change in batch) run.Apply(change);
        }
        using var writer = new SemaphoreSlim(1);
        await using var server = await Server.Start(database.DataSource, live: true, configure: app =>
        {
            app.MapReservationQueueEndpoints();
            app.MapPost("/api/test/graph/stream", async (HttpContext context) =>
            {
                await writer.WaitAsync(context.RequestAborted);
                try
                {
                    var before = run.Snapshot.LastSequence;
                    var clock = Stopwatch.StartNew();
                    const int count = 1000;
                    var projected = RunState.Restore(run.Snapshot, [], _ => { });
                    var changes = new List<RunEvent>(count);
                    var pending = new List<Task<GroupCommitReceipt>>(count);
                    try
                    {
                        for (var index = 0; index < count; index++)
                        {
                            var delay = TimeSpan.FromMilliseconds(index * 8) - clock.Elapsed;
                            if (delay > TimeSpan.Zero) await Task.Delay(delay, context.RequestAborted);
                            var key = definition.Nodes[index % definition.Nodes.Length].Key;
                            var next = projected.Snapshot.Nodes[key] switch
                            {
                                ExecutionStatus.Running => ExecutionStatus.Retrying,
                                ExecutionStatus.Retrying => ExecutionStatus.Dispatched,
                                ExecutionStatus.Dispatched => ExecutionStatus.Running,
                                _ => throw new InvalidOperationException("Unexpected graph fixture node state.")
                            };
                            var change = projected.Append(EventEntity.Node, key, (int)next, DateTimeOffset.UtcNow);
                            changes.Add(change);
                            pending.Add(appender.AppendAsync(change, context.RequestAborted));
                        }
                    }
                    finally { await Task.WhenAll(pending); }
                    foreach (var change in changes) run.Apply(change);
                    return Results.Ok(new { before, lastSeq = run.Snapshot.LastSequence, count,
                        elapsedMs = clock.Elapsed.TotalMilliseconds });
                }
                finally { writer.Release(); }
            }).RequireAuthorization();
        });
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "run-graph-browser.ts"));
        start.Environment["WF_GRAPH_API"] = server.Api;
        start.Environment["WF_GRAPH_TOKEN"] = server.Token(group: group);
        start.Environment["WF_GRAPH_RUN"] = run.Snapshot.RunId.ToString("D");
        start.Environment["WF_GRAPH_INITIAL_SEQ"] = run.Snapshot.LastSequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["WF_GRAPH_ARTIFACTS"] = Path.GetFullPath(Path.Combine(frontend!, "..", ".agent-artifacts",
            "run-graph-" + Guid.NewGuid().ToString("N")));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(4));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Actual run graph Chrome trace failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }
}
