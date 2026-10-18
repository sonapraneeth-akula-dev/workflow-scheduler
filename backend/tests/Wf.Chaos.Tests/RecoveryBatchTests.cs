using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using static Wf.Chaos.Tests.ScheduleExactlyOnceTests;

namespace Wf.Chaos.Tests;

public sealed class RecoveryBatchTests
{
    [Fact]
    public async Task Multiple_batches_restore_event_tails_and_incremental_commands_preserve_unaffected_runs()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await ChaosDatabase.StartAsync(token, requireCommitTimestamps: false);
        var workflow = await SeedAsync(database.Source, token);
        await using var versionQuery = database.Source.CreateCommand("SELECT id FROM workflow_versions");
        var version = (Guid)(await versionQuery.ExecuteScalarAsync(token))!;
        using var lease = new LeaseManager(database.Source, new("batch-leader", new Uri("http://localhost:5300")),
            NullLogger<LeaseManager>.Instance);
        await lease.StartAsync(token);
        await WaitAsync(() => lease.IsLeader, token);
        var store = new RunStore(database.Source, NullLogger<RunStore>.Instance, lease);
        var inbox = new CommandInbox(database.Source, TimeProvider.System);
        var ids = new List<Guid>();
        foreach (var _ in Enumerable.Range(0, 130))
        {
            var run = NewRun();
            ids.Add(run.Snapshot.RunId);
            await inbox.TriggerAsync(new(run.Snapshot, workflow, version, DateTimeOffset.UtcNow.AddHours(2)), "batch", token);
        }
        using var recovery = new RunRecovery(database.Source, store, lease, TimeProvider.System, NullLogger<RunRecovery>.Instance);
        await recovery.RecoverAsync(token);
        Assert.Equal(130, recovery.RecoveredRuns.Count);
        var first = recovery.RecoveredRuns[ids[0]].State;
        await using (var appender = new GroupCommitAppender(database.Source, NullLogger<GroupCommitAppender>.Instance,
            leadership: lease))
            await appender.AppendAsync(first.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), token);
        await recovery.RecoverAsync(token);
        Assert.Equal(RunStatus.Running, recovery.RecoveredRuns[ids[0]].State.Snapshot.Status);
        Assert.All(recovery.RecoveredRuns, pair =>
        {
            var timer = Assert.Single(pair.Value.Timers.Snapshot);
            Assert.Equal(pair.Key.ToString("D"), timer.Key.Key);
            Assert.Single(pair.Value.State.Snapshot.Nodes);
        });
        var preserved = recovery.RecoveredRuns[ids[1]];
        await recovery.StartAsync(token);
        try
        {
            var next = NewRun();
            await inbox.TriggerAsync(new(next.Snapshot, workflow, version, DateTimeOffset.UtcNow.AddHours(2)), "batch", token);
            await WaitAsync(() => recovery.RecoveredRuns.ContainsKey(next.Snapshot.RunId), token);
            Assert.Same(preserved, recovery.RecoveredRuns[ids[1]]);
            await inbox.AbortAsync(ids[2], new("batch", "Batch", DateTimeOffset.UtcNow, "stop", AbortSource.CLI), token);
            await WaitAsync(() => !recovery.RecoveredRuns.ContainsKey(ids[2]), token);
            Assert.Same(preserved, recovery.RecoveredRuns[ids[1]]);
            Assert.Equal(RunStatus.Aborted, (await store.LoadAsync(ids[2], token)).Snapshot.Status);
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
            await lease.StopAsync(CancellationToken.None);
        }
    }

    private static RunState NewRun() => new(Guid.CreateVersion7(),
        [new NodeDefinition("node", [], [new TaskDefinition("task")])],
        new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);

    private static async Task WaitAsync(Func<bool> condition, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
