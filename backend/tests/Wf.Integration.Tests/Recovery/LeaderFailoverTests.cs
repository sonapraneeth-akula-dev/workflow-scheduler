using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using Wf.Integration.Tests.Agents;
using Wf.Recovery.Fixture;

namespace Wf.Integration.Tests.Recovery;

public sealed class LeaderFailoverTests(FailoverDatabase database) : IClassFixture<FailoverDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance);

    [Fact]
    public async Task Killed_leader_is_replaced_by_second_process_and_recovered_run_completes_within_thirty_seconds()
    {
        await Reset();
        var run = NewRun();
        await new CommandInbox(database.DataSource, TimeProvider.System).TriggerAsync(
            new(run.Snapshot, "tests/failover", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5)), "author", Token);
        using var first = StartWorker("first", hold: true);
        Process? second = null;
        try
        {
            await Wait(async () => (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["node/task"] == ExecutionStatus.Running,
                TimeSpan.FromSeconds(15), first);
            second = StartWorker("second", hold: false);
            await Wait(async () =>
            {
                await using var command = database.DataSource.CreateCommand(
                    "SELECT count(*) FROM pg_stat_activity WHERE application_name='recovery-second'");
                return (long)(await command.ExecuteScalarAsync(Token) ?? 0L) > 0;
            }, TimeSpan.FromSeconds(10), second);
            var before = await Store.LoadAsync(run.Snapshot.RunId, Token);
            Assert.Equal(RunStatus.Running, before.Snapshot.Status);
            var elapsed = Stopwatch.StartNew();
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(Token);
            await Wait(async () => (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Status == RunStatus.Succeeded,
                TimeSpan.FromSeconds(30), second);
            Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(30), elapsed.Elapsed.ToString());
            var completed = await Store.LoadAsync(run.Snapshot.RunId, Token);
            Assert.Equal(ExecutionStatus.Succeeded, completed.Snapshot.Tasks["node/task"]);
            Assert.Equal(ExecutionStatus.Succeeded, completed.Snapshot.Nodes["node"]);
            Assert.Equal(10, completed.Snapshot.LastSequence);
            await using var events = database.DataSource.CreateCommand("SELECT count(*) FROM run_events WHERE run_id=$1");
            events.Parameters.AddWithValue(run.Snapshot.RunId);
            Assert.Equal(10L, await events.ExecuteScalarAsync(Token));
            await using var epoch = database.DataSource.CreateCommand("SELECT epoch FROM leases WHERE name='engine'");
            Assert.Equal(2L, await epoch.ExecuteScalarAsync(Token));
        }
        finally
        {
            await Stop(first);
            if (second is not null) { await Stop(second); second.Dispose(); }
        }
    }

    [Fact]
    public async Task Outage_keeps_acknowledgements_pending_backpressures_queue_and_commits_after_reconnection()
    {
        await Reset();
        var runs = new[] { await Create(), await Create(), await Create() };
        await using var proxy = new PostgresFaultProxy(database.ReconnectConnectionString);
        await using var source = NpgsqlDataSource.Create(proxy.ConnectionString);
        await using var appender = new GroupCommitAppender(source, NullLogger<GroupCommitAppender>.Instance,
            maxBatchSize: 1, queueCapacity: 1);
        proxy.SetAvailable(false);
        var pending = runs.Select(run => appender.AppendAsync(
            run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token)).ToArray();
        await Task.Delay(400, Token);
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        foreach (var run in runs) Assert.Equal(0, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        var elapsed = Stopwatch.StartNew();
        proxy.SetAvailable(true);
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        foreach (var run in runs) Assert.Equal(RunStatus.Running, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Status);
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_retries_identical_batch_without_duplicate_events()
    {
        await Reset();
        var run = await Create();
        await using var proxy = new PostgresFaultProxy(database.ReconnectConnectionString);
        await using var source = NpgsqlDataSource.Create(proxy.ConnectionString);
        await using var appender = new GroupCommitAppender(source, NullLogger<GroupCommitAppender>.Instance);
        proxy.DropNextCommitAcknowledgement();
        var receipt = await appender.AppendAsync(
            run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, proxy.DroppedCommitCount);
        Assert.Equal(1, receipt.EventCount);
        Assert.Equal(1, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM run_events WHERE run_id=$1");
        count.Parameters.AddWithValue(run.Snapshot.RunId);
        Assert.Equal(1L, await count.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task Only_exact_full_replay_is_idempotent_conflicting_or_partial_batch_rolls_back()
    {
        await Reset();
        var run = await Create();
        var other = await Create();
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        var change = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow);
        await appender.AppendAsync(change, Token);
        await appender.AppendAsync(change, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => appender.AppendAsync(change with { At = change.At.AddSeconds(1) }, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => appender.AppendAsync([
            change, other.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow)
        ], Token));
        Assert.Equal(0, (await Store.LoadAsync(other.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(1, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
    }

    [Fact]
    public async Task Shutdown_during_outage_is_bounded_and_never_reports_uncommitted_events_as_success()
    {
        await Reset();
        var run = await Create();
        await using var proxy = new PostgresFaultProxy(database.ReconnectConnectionString);
        await using var source = NpgsqlDataSource.Create(proxy.ConnectionString);
        var appender = new GroupCommitAppender(source, NullLogger<GroupCommitAppender>.Instance);
        proxy.SetAvailable(false);
        var pending = appender.AppendAsync(run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token);
        await Task.Delay(200, Token);
        var elapsed = Stopwatch.StartNew();
        await appender.DisposeAsync();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(7));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
    }

    [Fact]
    public async Task Postgres_restart_retains_buffered_events_and_resumes_within_outage_plus_thirty_seconds()
    {
        if (!database.CanRestart) Assert.Skip("Restart requires this fixture's disposable PostgreSQL container, not a shared external server.");
        await Reset();
        var run = await Create();
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ReconnectConnectionString) {
            Timeout = 1, CommandTimeout = 3
        }.ConnectionString);
        await using var appender = new GroupCommitAppender(source, NullLogger<GroupCommitAppender>.Instance);
        await database.StopPostgresAsync(Token);
        Task<GroupCommitReceipt>? pending = null;
        try
        {
            pending = appender.AppendAsync(run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow), Token);
            await Task.Delay(1000, Token);
            Assert.False(pending.IsCompleted);
        }
        finally { await database.StartPostgresAsync(Token); }
        var elapsed = Stopwatch.StartNew();
        await pending!.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(30));
        Assert.Equal(RunStatus.Running, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Status);
    }

    private Process StartWorker(string holder, bool hold)
    {
        var assembly = typeof(LeaderFailoverTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(FixtureMarker).Assembly.Location })
            start.ArgumentList.Add(argument);
        start.Environment["WF_RECOVERY_DATABASE"] = new NpgsqlConnectionStringBuilder(database.ReconnectConnectionString) {
            ApplicationName = $"recovery-{holder}"
        }.ConnectionString;
        start.Environment["WF_RECOVERY_HOLDER"] = holder;
        start.Environment["WF_RECOVERY_HOLD"] = hold ? "true" : "false";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Recovery fixture did not start.");
        return process;
    }

    private async Task Wait(Func<Task<bool>> condition, TimeSpan timeout, Process process)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Recovery fixture exited ({process.ExitCode}): {await process.StandardError.ReadToEndAsync(Token)}");
            try { if (await condition()) return; }
            catch (KeyNotFoundException) { /* Trigger has not been processed yet. */ }
            await Task.Delay(25, Token);
        }
        Assert.Fail($"Recovery condition did not complete within {timeout}.");
    }
    private static async Task Stop(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    private async Task Reset()
    {
        await using var command = database.DataSource.CreateCommand("TRUNCATE commands,runs CASCADE; DELETE FROM leases");
        await command.ExecuteNonQueryAsync(Token);
    }
    private static RunState NewRun() => new(Guid.CreateVersion7(), [new("node", [], [new("task")])],
        new WorkflowMetadata("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
    private async Task<RunState> Create()
    {
        var run = NewRun();
        await Store.CreateAsync(run, "tests/failover", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
        return run;
    }
}

public sealed class FailoverDatabase : AgentDatabase
{
    protected override PostgreSqlBuilder ConfigurePostgres(PostgreSqlBuilder builder)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        // A fixed binding preserves the primary endpoint across a real container restart.
        return builder.WithPortBinding(port, 5432);
    }
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0009_leases.sql", "0010_commands.sql"];
    public bool CanRestart => Postgres is not null;
    public string ReconnectConnectionString => DatabaseConnectionString;
    public Task StopPostgresAsync(CancellationToken cancellationToken) =>
        (Postgres ?? throw new InvalidOperationException("Cannot stop a shared external PostgreSQL server.")).StopAsync(cancellationToken);
    public async Task StartPostgresAsync(CancellationToken cancellationToken)
    {
        var postgres = Postgres ?? throw new InvalidOperationException("Cannot start a shared external PostgreSQL server.");
        await postgres.StartAsync(cancellationToken);
        Assert.Equal(new NpgsqlConnectionStringBuilder(DatabaseConnectionString).Port,
            new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Port);
        DataSource.Clear();
    }
}
