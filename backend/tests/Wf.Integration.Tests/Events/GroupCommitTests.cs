using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.Engine.Core;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Events;

public sealed class GroupCommitTests(EventDatabase database) : IClassFixture<EventDatabase>
{
    private readonly RunStore store = new(database.DataSource, NullLogger<RunStore>.Instance);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Atomic_actor_events_commit_together_and_restore_from_snapshot_plus_tail()
    {
        var run = await Create();
        var expected = RunState.Restore(run.Snapshot, [], Assert.Fail);
        var at = DateTimeOffset.UtcNow;
        var events = new[]
        {
            expected.Append(EventEntity.Run, null, (int)RunStatus.Running, at),
            expected.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, at),
            expected.Append(EventEntity.Node, "node", (int)ExecutionStatus.Dispatched, at),
            expected.Append(EventEntity.Node, "node", (int)ExecutionStatus.Running, at),
            expected.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, at)
        };
        await using var appender = Appender();
        var receipt = await appender.AppendAsync(events, Token);
        Assert.Equal(events.Length, receipt.EventCount);
        Assert.True(receipt.PersistenceTime > TimeSpan.Zero);
        Assert.Equal(0, run.Snapshot.LastSequence);
        var restored = await store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(expected.Snapshot.LastSequence, restored.Snapshot.LastSequence);
        Assert.Equal(expected.Snapshot.Status, restored.Snapshot.Status);
        Assert.Equal(expected.Snapshot.Nodes, restored.Snapshot.Nodes);
        Assert.Equal(expected.Snapshot.Tasks, restored.Snapshot.Tasks);
        await store.AppendAsync(restored, EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, at, Token);
        Assert.Equal(6, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
    }

    [Fact]
    public async Task Failed_sequence_rolls_back_every_run_in_an_atomic_request_and_later_writes_still_work()
    {
        var first = await Create();
        var second = await Create();
        var good = first.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow);
        var bad = second.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow) with { Sequence = 2 };
        await using var appender = Appender();
        await Assert.ThrowsAsync<InvalidOperationException>(() => appender.AppendAsync([good, bad], Token));
        Assert.Equal(0, (await store.LoadAsync(first.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(0, (await store.LoadAsync(second.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(1, (await appender.AppendAsync(good, Token)).EventCount);
        Assert.Equal(RunStatus.Running, (await store.LoadAsync(first.Snapshot.RunId, Token)).Snapshot.Status);
    }

    [Fact]
    public async Task Blocked_database_never_acknowledges_and_queued_cancellation_never_persists()
    {
        var first = await Create();
        var second = await Create();
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using (var command = new NpgsqlCommand("LOCK TABLE run_events IN ACCESS EXCLUSIVE MODE", connection))
            await command.ExecuteNonQueryAsync(Token);
        await using var appender = Appender();
        using var committing = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = appender.AppendAsync(first.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), committing.Token);
        await WaitForInsertLock();
        await committing.CancelAsync();
        Assert.False(pending.IsCompleted);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var skipped = appender.AppendAsync(second.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), cancelled.Token);
        await cancelled.CancelAsync();
        await transaction.CommitAsync(Token);
        await pending;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped);
        Assert.Equal(1, (await store.LoadAsync(first.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(0, (await store.LoadAsync(second.Snapshot.RunId, Token)).Snapshot.LastSequence);
    }

    [Fact]
    public async Task Queued_actors_share_commits_without_exceeding_the_batch_cap()
    {
        var first = await Create();
        var runs = new List<RunState>();
        for (var index = 0; index < 32; index++) runs.Add(await Create());
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using (var command = new NpgsqlCommand("LOCK TABLE run_events IN ACCESS EXCLUSIVE MODE", connection))
            await command.ExecuteNonQueryAsync(Token);
        await using var appender = Appender(maxBatchSize: 8);
        var blocked = appender.AppendAsync(first.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token);
        await WaitForInsertLock();
        var pending = runs.Select(run => appender.AppendAsync(
            run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token)).ToArray();
        await transaction.CommitAsync(Token);
        await blocked;
        var receipts = await Task.WhenAll(pending);
        Assert.All(receipts, receipt => Assert.Equal(8, receipt.EventCount));
        Assert.Equal(4, receipts.DistinctBy(receipt => receipt.BatchId).Count());
    }

    [Fact]
    public async Task Dispose_drains_accepted_requests_and_rejects_new_work()
    {
        var run = await Create();
        var appender = Appender();
        var pending = appender.AppendAsync(run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token);
        await appender.DisposeAsync();
        Assert.Equal(1, (await pending).EventCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => appender.AppendAsync(
            run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token));
        Assert.Equal(1, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Requested_collection_window_is_bounded_and_grouping_caps_events(int milliseconds)
    {
        var run = await Create();
        await using var appender = Appender(TimeSpan.FromMilliseconds(milliseconds), maxBatchSize: 1);
        var receipt = await appender.AppendAsync(run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token);
        Assert.Equal(1, receipt.EventCount);
        await Assert.ThrowsAsync<ArgumentException>(() => appender.AppendAsync(
            [new(run.Snapshot.RunId, 2, DateTimeOffset.UtcNow, EventEntity.Task, "node/task", 1),
                new(run.Snapshot.RunId, 3, DateTimeOffset.UtcNow, EventEntity.Task, "node/task", 2)], Token));
    }

    [Fact]
    public void Invalid_collection_window_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Appender(TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Appender(TimeSpan.FromMilliseconds(3)));
    }

    private GroupCommitAppender Appender(TimeSpan? window = null, int maxBatchSize = 512) =>
        new(database.DataSource, NullLogger<GroupCommitAppender>.Instance, window: window, maxBatchSize: maxBatchSize);

    private async Task<RunState> Create()
    {
        var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
            new WorkflowMetadata("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, "tests/group-commit", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
        return run;
    }

    private async Task WaitForInsertLock()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = database.DataSource.CreateCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock'
                    AND query LIKE '%INSERT INTO run_events%'
                """);
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! > 0) return;
            await Task.Delay(10, timeout.Token);
        }
    }
}

public sealed class EventDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => ["0002_runs_events.sql", "0003_attempt_timers.sql"];
}
