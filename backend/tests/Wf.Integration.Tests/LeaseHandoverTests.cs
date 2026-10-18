using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Hosting;
using Wf.Engine.Core;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests;

public sealed class LeaseHandoverTests(LeaseHandoverDatabase database, ITestOutputHelper output)
    : IClassFixture<LeaseHandoverDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(600)]
    public async Task GracefulHostStopDrainsBeforeReleaseAndStandbyRecoversWithinThreeSeconds(int standbyPhaseMs)
    {
        await using (var reset = database.DataSource.CreateCommand("TRUNCATE commands,runs CASCADE; DELETE FROM leases"))
            await reset.ExecuteNonQueryAsync(Token);
        using var first = BuildHost(5300);
        using var second = BuildHost(5301);
        var leader = first.Services.GetRequiredService<LeaseManager>();
        var standby = second.Services.GetRequiredService<LeaseManager>();
        var appender = first.Services.GetRequiredService<GroupCommitAppender>();
        var recovery = second.Services.GetRequiredService<RunRecovery>();
        try
        {
            await first.StartAsync(Token);
            await Wait(() => leader.IsLeader && first.Services.GetRequiredService<RunRecovery>().IsReady);
            var oldLease = leader.RequireLease();
            var store = first.Services.GetRequiredService<RunStore>();
            var runs = new List<RunState>();
            for (var index = 0; index < 16; index++)
            {
                var run = new RunState(Guid.CreateVersion7(), [new NodeDefinition("node", [], [new("task")])],
                    new WorkflowMetadata("handover", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
                await store.CreateAsync(run, "tests/handover", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
                runs.Add(run);
            }
            await second.StartAsync(Token);
            await Wait(() => standby.LeaderHint == leader.LeaderHint);
            Assert.False(standby.IsLeader);
            await Task.Delay(standbyPhaseMs, Token);

            // A real PostgreSQL lock proves accepted work is still buffered when graceful host shutdown begins.
            await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
            await using (var transaction = await connection.BeginTransactionAsync(Token))
            {
                await using (var block = new NpgsqlCommand("LOCK TABLE run_events IN ACCESS EXCLUSIVE MODE", connection, transaction))
                    await block.ExecuteNonQueryAsync(Token);
                var pending = new List<Task<GroupCommitReceipt>> { appender.AppendAsync(StartEvents(runs[0]), Token) };
                await WaitForBlockedAppender();
                pending.AddRange(runs.Skip(1).Select(run => appender.AppendAsync(StartEvents(run), Token)));
                Assert.All(pending, task => Assert.False(task.IsCompleted));

                // On Windows this exercises the same IHost shutdown pipeline as ConsoleLifetime, not an OS SIGTERM.
                var elapsed = Stopwatch.StartNew();
                var stopping = first.StopAsync(Token);
                await Task.Delay(100, Token);
                Assert.False(stopping.IsCompleted);
                Assert.True(leader.IsLeader);
                Assert.False(standby.IsLeader);
                await using (var held = database.DataSource.CreateCommand(
                    "SELECT holder=$1 AND epoch=$2 AND expires_at>clock_timestamp() FROM leases WHERE name='engine'"))
                {
                    held.Parameters.AddWithValue(oldLease.Holder);
                    held.Parameters.AddWithValue(oldLease.Epoch);
                    Assert.Equal(true, await held.ExecuteScalarAsync(Token));
                }

                await transaction.CommitAsync(Token);
                await stopping;
                var receipts = await Task.WhenAll(pending);
                Assert.Equal(runs.Count * 3, receipts.DistinctBy(receipt => receipt.BatchId).Sum(receipt => receipt.EventCount));
                Assert.False(leader.IsLeader);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => appender.AppendAsync(
                    runs[0].CreateEvent(EventEntity.Node, "node", (int)ExecutionStatus.Dispatched, DateTimeOffset.UtcNow), Token));
                Assert.False(await leader.TryAcquireOrRenewAsync(Token));
                await Wait(() => standby.IsLeader);
                var takeoverGap = elapsed.Elapsed;
                await Wait(() => recovery.IsReady);
                output.WriteLine("Graceful IHost.StopAsync: phase={0}ms, takeover={1:F1}ms, recovered={2:F1}ms, epoch={3}->{4}",
                    standbyPhaseMs, takeoverGap.TotalMilliseconds, elapsed.Elapsed.TotalMilliseconds,
                    oldLease.Epoch, standby.RequireLease().Epoch);
                Assert.InRange(takeoverGap, TimeSpan.Zero, TimeSpan.FromSeconds(3));
                Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
            }

            Assert.Equal(oldLease.Epoch + 1, standby.RequireLease().Epoch);
            Assert.Equal(runs.Count, recovery.RecoveredRuns.Count);
            await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
            await using (var transaction = await connection.BeginTransactionAsync(Token))
                await Assert.ThrowsAsync<NotLeaderException>(() => leader.FenceAsync(connection, transaction, oldLease, Token));

            var newAppender = second.Services.GetRequiredService<GroupCommitAppender>();
            var newStore = second.Services.GetRequiredService<RunStore>();
            foreach (var original in runs)
            {
                var recovered = recovery.RecoveredRuns[original.Snapshot.RunId].State;
                Assert.Equal(original.Snapshot.LastSequence, recovered.Snapshot.LastSequence);
                Assert.Equal(original.Snapshot.Status, recovered.Snapshot.Status);
                Assert.Equal(original.Snapshot.Nodes, recovered.Snapshot.Nodes);
                Assert.Equal(original.Snapshot.Tasks, recovered.Snapshot.Tasks);
                await newAppender.AppendAsync(FinishEvents(recovered), Token);
                var completed = (await newStore.LoadAsync(original.Snapshot.RunId, Token)).Snapshot;
                Assert.Equal(RunStatus.Succeeded, completed.Status);
                Assert.All(completed.Tasks.Values, status => Assert.Equal(ExecutionStatus.Succeeded, status));
            }
            await using var failures = database.DataSource.CreateCommand(
                "SELECT count(*) FROM runs WHERE status<>$1");
            failures.Parameters.AddWithValue((short)RunStatus.Succeeded);
            Assert.Equal(0L, await failures.ExecuteScalarAsync(Token));
        }
        finally
        {
            await second.StopAsync(CancellationToken.None);
            await first.StopAsync(CancellationToken.None);
        }
    }

    private IHost BuildHost(int port)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new LeaseOptions(Guid.NewGuid().ToString("N"), new Uri($"http://localhost:{port}")));
        builder.Services.AddSingleton<LeaseManager>();
        builder.Services.AddHostedService<LeaseManager>(services => services.GetRequiredService<LeaseManager>());
        builder.Services.AddHostedService<EngineShutdownService>();
        builder.Services.AddSingleton<RunStore>();
        builder.Services.AddSingleton<RunRecovery>();
        builder.Services.AddHostedService<RunRecovery>(services => services.GetRequiredService<RunRecovery>());
        builder.Services.AddSingleton<GroupCommitAppender>();
        return builder.Build();
    }

    private static RunEvent[] StartEvents(RunState run) =>
    [
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow)
    ];

    private static RunEvent[] FinishEvents(RunState run) =>
    [
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Dispatched, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Running, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Running, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Succeeded, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Succeeded, DateTimeOffset.UtcNow),
        run.Append(EventEntity.Run, null, (int)RunStatus.Succeeded, DateTimeOffset.UtcNow)
    ];

    private async Task WaitForBlockedAppender()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            await using var command = database.DataSource.CreateCommand("""
                SELECT EXISTS(
                    SELECT 1 FROM pg_stat_activity a JOIN pg_locks l ON l.pid=a.pid
                    WHERE a.datname=current_database() AND a.wait_event_type='Lock'
                        AND l.relation='run_events'::regclass AND NOT l.granted
                        AND a.query LIKE '%INSERT INTO run_events%'
                )
                """);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(5, timeout.Token);
        }
    }

    private async Task Wait(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}

public sealed class LeaseHandoverDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0009_leases.sql", "0010_commands.sql"];
}
