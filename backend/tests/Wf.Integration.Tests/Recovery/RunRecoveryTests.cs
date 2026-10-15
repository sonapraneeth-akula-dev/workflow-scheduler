using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using Wf.Engine.Core.Timers;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Recovery;

public sealed class RunRecoveryTests(RecoveryDatabase database) : IClassFixture<RecoveryDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly RecoveryTime time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Snapshot_plus_committed_tail_matches_precrash_state_and_excludes_terminal_runs()
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var store = Store(leader);
        var run = await Create(store);
        var terminal = await Create(store);
        await store.AppendAsync(terminal, EventEntity.Run, null, (int)RunStatus.Aborted, time.GetUtcNow(), Token);
        await using (var appender = new GroupCommitAppender(database.DataSource,
            NullLogger<GroupCommitAppender>.Instance, leadership: leader))
        {
            var changes = new[]
            {
                run.Append(EventEntity.Run, null, (int)RunStatus.Running, time.GetUtcNow()),
                run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, time.GetUtcNow()),
                run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, time.GetUtcNow()),
                run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, time.GetUtcNow()),
                run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Running, time.GetUtcNow())
            };
            await appender.AppendAsync(changes, Token);
        }
        using var recovery = Recovery(store, leader);
        await recovery.RecoverAsync(Token);
        Assert.True(recovery.IsReady);
        var recovered = Assert.Single(recovery.RecoveredRuns).Value;
        Assert.Equal(run.Snapshot.RunId, recovered.State.Snapshot.RunId);
        Assert.Equal(run.Snapshot.LastSequence, recovered.State.Snapshot.LastSequence);
        Assert.Equal(run.Snapshot.Status, recovered.State.Snapshot.Status);
        Assert.Equal(run.Snapshot.Nodes, recovered.State.Snapshot.Nodes);
        Assert.Equal(run.Snapshot.Tasks, recovered.State.Snapshot.Tasks);
        Assert.Equal(run.Snapshot.Variables, recovered.State.Snapshot.Variables);
        Assert.Single(recovered.Timers.Snapshot);
    }

    [Fact]
    public async Task Deadline_retry_cancel_and_kill_absolute_times_are_rearmed_without_restarting_grace()
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var store = Store(leader);
        var run = await Create(store, ["task", "cancel", "kill", "retry"]);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, time.GetUtcNow(), Token);
        await store.AppendAsync(run, EventEntity.Node, "node", (int)ExecutionStatus.Ready, time.GetUtcNow(), Token);
        foreach (var key in run.Snapshot.Tasks.Keys.ToArray())
        {
            await store.AppendAsync(run, EventEntity.Task, key, (int)ExecutionStatus.Ready, time.GetUtcNow(), Token);
            await store.AppendAsync(run, EventEntity.Task, key, (int)ExecutionStatus.Dispatched, time.GetUtcNow(), Token);
            await store.AppendAsync(run, EventEntity.Task, key, (int)ExecutionStatus.Running, time.GetUtcNow(), Token);
        }
        await store.AppendAsync(run, EventEntity.Task, "node/retry", (int)ExecutionStatus.Retrying, time.GetUtcNow(), Token);
        var now = time.GetUtcNow();
        await Execute("UPDATE node_runs SET deadline_at=$2 WHERE run_id=$1", run.Snapshot.RunId, now.AddMinutes(5));
        await Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,retry_at,cancel_requested_at,kill_at,timer_phase)
            VALUES(uuid_v7_run_day($1),$1,'node/task',1,3,$2,NULL,NULL,NULL,0),
                  (uuid_v7_run_day($1),$1,'node/cancel',1,3,$2,NULL,$3,$4,1),
                  (uuid_v7_run_day($1),$1,'node/kill',1,3,$2,NULL,$3,$4,2),
                  (uuid_v7_run_day($1),$1,'node/retry',1,7,$2,$5,NULL,NULL,0)
            """, run.Snapshot.RunId, now.AddMinutes(1), now.AddSeconds(-10),
            now.AddSeconds(20), now.AddSeconds(15));
        using var recovery = Recovery(store, leader);
        await recovery.RecoverAsync(Token);
        var timers = recovery.RecoveredRuns[run.Snapshot.RunId].Timers;
        Assert.Equal(6, timers.Snapshot.Length);
        Assert.Empty(timers.GetDue());
        time.Advance(TimeSpan.FromSeconds(21));
        var due = timers.GetDue();
        Assert.Equal(2, due.Length);
        Assert.Contains(due, action => action.Kind == TimerActionKind.DispatchRetry && action.Before.Key.Key == "node/retry");
        var kill = Assert.Single(due, action => action.Kind == TimerActionKind.Kill);
        Assert.Equal("node/cancel/1", kill.Before.Key.Key);
        Assert.Equal(now.AddSeconds(20), kill.Before.KillAt);
        // Repeated recovery preserves committed phases; observing due proposals never mutates them.
        await recovery.RecoverAsync(Token);
        Assert.Equal(due, recovery.RecoveredRuns[run.Snapshot.RunId].Timers.GetDue());
        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Contains(timers.GetDue(), action => action.Kind == TimerActionKind.Cancel && action.Before.Key.Key == "node/task/1");
    }

    [Fact]
    public async Task Pending_trigger_is_created_once_even_if_its_deadline_elapsed_during_outage()
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var store = Store(leader);
        var inbox = new CommandInbox(database.DataSource, time);
        var initial = NewRun();
        var trigger = new TriggerRunCommand(initial.Snapshot, "team/type/test", Guid.CreateVersion7(), time.GetUtcNow().AddMinutes(1));
        var id = await inbox.TriggerAsync(trigger, "author", Token);
        Assert.Equal(id, await inbox.TriggerAsync(trigger, "author", Token));
        time.Advance(TimeSpan.FromHours(2));
        using var recovery = Recovery(store, leader);
        await recovery.RecoverAsync(Token);
        Assert.Equal(RunStatus.Queued, recovery.RecoveredRuns[initial.Snapshot.RunId].State.Snapshot.Status);
        Assert.Single(recovery.RecoveredRuns[initial.Snapshot.RunId].Timers.GetDue());
        await recovery.RecoverAsync(Token);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE processed_at IS NOT NULL"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs"));
        Assert.Equal("author", await Scalar("SELECT triggered_by FROM runs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_abort_preserves_first_identity_and_is_applied_once(bool running)
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var store = Store(leader);
        var run = await Create(store);
        if (running) await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, time.GetUtcNow(), Token);
        var inbox = new CommandInbox(database.DataSource, time);
        var accepted = new AbortRecord("first", "First", time.GetUtcNow(), "stop", AbortSource.CLI);
        var id = await inbox.AbortAsync(run.Snapshot.RunId, accepted, Token);
        Assert.Equal(id, await inbox.AbortAsync(run.Snapshot.RunId,
            new("second", "Second", time.GetUtcNow(), "different", AbortSource.API), Token));
        using var recovery = Recovery(store, leader);
        await recovery.RecoverAsync(Token);
        var loaded = await store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(accepted, loaded.Snapshot.Abort);
        Assert.Equal(running ? RunStatus.Aborting : RunStatus.Aborted, loaded.Snapshot.Status);
        var sequence = loaded.Snapshot.LastSequence;
        await recovery.RecoverAsync(Token);
        Assert.Equal(sequence, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(running, recovery.RecoveredRuns.ContainsKey(run.Snapshot.RunId));
        if (!running) Assert.All(loaded.Snapshot.Tasks.Values, status => Assert.Equal(ExecutionStatus.Skipped, status));
    }

    [Fact]
    public async Task Same_timestamp_trigger_is_processed_before_its_abort()
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var inbox = new CommandInbox(database.DataSource, time);
        var initial = NewRun();
        await inbox.TriggerAsync(new(initial.Snapshot, "team/type/test", Guid.CreateVersion7(),
            time.GetUtcNow().AddHours(1)), "author", Token);
        await inbox.AbortAsync(initial.Snapshot.RunId,
            new("author", "Author", time.GetUtcNow(), "stop", AbortSource.CLI), Token);
        using var recovery = Recovery(Store(leader), leader);
        await recovery.RecoverAsync(Token);
        Assert.True(recovery.IsReady);
        Assert.Empty(recovery.RecoveredRuns);
        Assert.Equal(RunStatus.Aborted, (await Store(leader).LoadAsync(initial.Snapshot.RunId, Token)).Snapshot.Status);
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM commands WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task Failed_command_commit_rolls_back_events_and_processing_marker_before_retry()
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var store = Store(leader);
        var run = await Create(store);
        await new CommandInbox(database.DataSource, time).AbortAsync(run.Snapshot.RunId,
            new("operator", "Operator", time.GetUtcNow(), "stop", AbortSource.API), Token);
        await Execute("ALTER TABLE run_events ADD CONSTRAINT reject_recovery CHECK (false)");
        using var recovery = Recovery(store, leader);
        try { await Assert.ThrowsAsync<PostgresException>(() => recovery.RecoverAsync(Token)); }
        finally { await Execute("ALTER TABLE run_events DROP CONSTRAINT reject_recovery"); }
        Assert.False(recovery.IsReady);
        Assert.Empty(recovery.RecoveredRuns);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE processed_at IS NOT NULL"));
        Assert.Equal(0, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        await recovery.RecoverAsync(Token);
        Assert.Equal(RunStatus.Aborted, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Status);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task Fenced_leader_cannot_recover_or_ack_pending_commands_and_new_epoch_can()
    {
        await Reset();
        using var first = Manager();
        using var second = Manager();
        Assert.True(await first.TryAcquireOrRenewAsync(Token));
        var run = await Create(Store(first));
        using var old = Recovery(Store(first), first);
        await old.RecoverAsync(Token);
        await new CommandInbox(database.DataSource, time).AbortAsync(run.Snapshot.RunId,
            new("operator", "Operator", time.GetUtcNow(), "stop", AbortSource.API), Token);
        await Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.True(await second.TryAcquireOrRenewAsync(Token));
        await Assert.ThrowsAsync<NotLeaderException>(() => old.RecoverAsync(Token));
        Assert.False(old.IsReady);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE processed_at IS NOT NULL"));
        using var recovered = Recovery(Store(second), second);
        await recovered.RecoverAsync(Token);
        Assert.True(recovered.IsReady);
        Assert.Equal(RunStatus.Aborted, (await Store(second).LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Status);
    }

    [Fact]
    public async Task Hosted_recovery_waits_for_acquisition_drains_new_commands_and_recovers_next_epoch()
    {
        await Reset();
        using var leader = Manager();
        var store = Store(leader);
        using var recovery = Recovery(store, leader);
        await recovery.StartAsync(Token);
        try
        {
            Assert.False(recovery.IsReady);
            Assert.True(await leader.TryAcquireOrRenewAsync(Token));
            await Wait(() => recovery.IsReady);
            var initial = NewRun();
            await new CommandInbox(database.DataSource, time).TriggerAsync(
                new(initial.Snapshot, "team/type/test", Guid.CreateVersion7(), time.GetUtcNow().AddHours(1)), "author", Token);
            await Wait(() => recovery.RecoveredRuns.ContainsKey(initial.Snapshot.RunId));
            await leader.ReleaseAsync(Token);
            Assert.False(recovery.IsReady);
            Assert.Empty(recovery.RecoveredRuns);
            Assert.True(await leader.TryAcquireOrRenewAsync(Token));
            await Wait(() => recovery.IsReady);
            Assert.Contains(initial.Snapshot.RunId, recovery.RecoveredRuns.Keys);
        }
        finally { await recovery.StopAsync(Token); }
    }

    [Fact]
    public async Task Invalid_inbox_payload_remains_pending_and_never_makes_engine_ready()
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        await Execute("""
            INSERT INTO commands(id,kind,run_id,payload,created_by,created_at)
            VALUES($1,0,$2,'{}','author',clock_timestamp())
            """, Guid.CreateVersion7(), Guid.CreateVersion7());
        using var recovery = Recovery(Store(leader), leader);
        await Assert.ThrowsAsync<ArgumentNullException>(() => recovery.RecoverAsync(Token));
        Assert.False(recovery.IsReady);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task Inbox_rejects_invalid_identity_deadline_and_noninitial_snapshots()
    {
        var inbox = new CommandInbox(database.DataSource, time);
        var initial = NewRun();
        var trigger = new TriggerRunCommand(initial.Snapshot, "team/type/test", Guid.CreateVersion7(), time.GetUtcNow().AddHours(1));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.TriggerAsync(trigger with { DeadlineAt = time.GetUtcNow() }, "author", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.TriggerAsync(trigger with { VersionId = Guid.Empty }, "author", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.TriggerAsync(trigger with {
            InitialState = initial.Snapshot with { LastSequence = 1 }
        }, "author", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.AbortAsync(Guid.NewGuid(),
            new("author", "Author", time.GetUtcNow(), "stop", AbortSource.CLI), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.AbortAsync(initial.Snapshot.RunId,
            new("author", "Author", time.GetUtcNow().AddSeconds(1), "stop", AbortSource.CLI), Token));
    }

    private LeaseManager Manager() => new(database.DataSource,
        new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5200")), NullLogger<LeaseManager>.Instance);
    private RunStore Store(LeaseManager manager) => new(database.DataSource, NullLogger<RunStore>.Instance, manager);
    private RunRecovery Recovery(RunStore store, LeaseManager manager) =>
        new(database.DataSource, store, manager, time, NullLogger<RunRecovery>.Instance);
    private static RunState NewRun(string[]? tasks = null) => new(Guid.CreateVersion7(),
        [new NodeDefinition("node", [], (tasks ?? ["task"]).Select(key => new TaskDefinition(key)).ToImmutableArray())],
        new WorkflowMetadata("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
    private async Task<RunState> Create(RunStore store, string[]? tasks = null)
    {
        var run = NewRun(tasks);
        await store.CreateAsync(run, "team/type/test", Guid.CreateVersion7(), time.GetUtcNow().AddHours(1), Token);
        return run;
    }
    private async Task Reset() => await Execute("TRUNCATE commands,runs CASCADE; DELETE FROM leases");
    private async Task Execute(string sql, params object[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await command.ExecuteNonQueryAsync(Token);
    }
    private async Task<object?> Scalar(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync(Token);
    }
    private async Task Wait(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class RecoveryTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now.AddTicks(-(now.Ticks % 10));
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current += elapsed;
    }
}

public sealed class RecoveryDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0009_leases.sql", "0010_commands.sql"];
}
