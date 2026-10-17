using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed class ConcurrentRunPreparationTests(HostedExecutionDatabase database) : IClassFixture<HostedExecutionDatabase>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_skips_history_for_current_snapshots_but_replays_a_committed_tail(bool trailingEvent)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var definition = Workflow.Define("tests/prepare/load", "1.0.0", workflow =>
            workflow.Type("test").Node("work", node => node.Task("task", _ => { })));
        var run = new Wf.Engine.Core.RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail);
        await store.CreateAsync(run, definition.Name, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), token);
        await using var connection = await database.DataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        if (trailingEvent)
        {
            var change = run.Append(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow);
            await using var write = new NpgsqlCommand("""
                WITH appended AS (
                    INSERT INTO run_events(run_day,run_id,seq,ts,kind,payload)
                    VALUES(uuid_v7_run_day($1),$1,$2,$3,0,$4)
                )
                UPDATE runs SET last_seq=$2,status=1 WHERE run_day=uuid_v7_run_day($1) AND id=$1
                """, connection, transaction);
            write.Parameters.AddWithValue(change.RunId);
            write.Parameters.AddWithValue(change.Sequence);
            write.Parameters.AddWithValue(change.At);
            write.Parameters.AddWithValue(JsonSerializer.SerializeToUtf8Bytes(change));
            await write.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
        }
        else
        {
            await using var locked = new NpgsqlCommand("LOCK TABLE run_events IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await locked.ExecuteNonQueryAsync(token);
        }
        var loaded = await store.LoadAsync(run.Snapshot.RunId, token);
        Assert.Equal(run.Snapshot.LastSequence, loaded.Snapshot.LastSequence);
        Assert.Equal(run.Snapshot.Status, loaded.Snapshot.Status);
        Assert.Equal(run.Snapshot.Tasks, loaded.Snapshot.Tasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_durable_run_preparation_preserves_real_renewing_leadership(bool startRecovery)
    {
        var token = TestContext.Current.CancellationToken;
        using var leadership = new LeaseManager(database.DataSource,
            new("prepare-" + Guid.NewGuid().ToString("N"), new Uri("http://localhost:5000")), NullLogger<LeaseManager>.Instance);
        await leadership.StartAsync(token);
        try
        {
            while (!leadership.IsLeader) await Task.Delay(10, token);
            var lease = leadership.RequireLease();
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leadership);
            using var recovery = new RunRecovery(database.DataSource, store, leadership, TimeProvider.System, NullLogger<RunRecovery>.Instance);
            if (startRecovery)
            {
                await recovery.StartAsync(token);
                while (!recovery.IsReady) await Task.Delay(10, token);
                await Task.Delay(500, token);
                await recovery.StopAsync(token);
            }
            var definition = Workflow.Define("tests/prepare/tiny", "1.0.0", workflow =>
                workflow.Type("test").Node("work", node => node.Task("task", _ => { })));
            foreach (var batch in Enumerable.Range(0, 1000).Chunk(32))
            {
                await Parallel.ForEachAsync(batch,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (_, stopping) =>
                {
                    if (leadership.ExecuteTask is { IsCompleted: true } failed) await failed;
                    var run = new Wf.Engine.Core.RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail);
                    await store.CreateAsync(run, definition.Name, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), stopping);
                    await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, stopping);
                    foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                        await store.AppendAsync(run, EventEntity.Node, "work", (int)state, DateTimeOffset.UtcNow, stopping);
                    await store.AppendAsync(run, EventEntity.Task, "work/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, stopping);
                });
                await Task.Delay(10, token);
                Assert.Equal(lease, leadership.RequireLease());
            }
            Assert.True(leadership.IsLeader);
            Assert.False(leadership.ExecuteTask!.IsCompleted);
        }
        finally { await leadership.StopAsync(CancellationToken.None); }
    }
}
