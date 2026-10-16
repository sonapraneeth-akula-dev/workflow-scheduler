using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Integration.Tests.Agents;
using Wf.Recovery.Fixture;
using Wf.Testing.RunQueries;

namespace Wf.Integration.Tests.Runs;

public sealed class AbortTests(AbortDatabase database) : IClassFixture<AbortDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortCli_actual_executable_observes_Aborting_then_Aborted_and_preserves_one_record_after_leader_kill(bool failover)
    {
        var seed = await Seed();
        await using var server = await RunQueryServer.Start(database.DataSource, Token, abort: true, live: true);
        using var first = StartWorker("first", failover);
        Process? second = null;
        Process? cli = null;
        try
        {
            await Wait(async () => await Scalar("SELECT holder FROM leases WHERE name='engine'") as string == "abort-first", first);
            var started = StartCli(server, seed.Run, server.Jwt(seed.Group));
            cli = started.Process;
            await ObservedAborting(started);
            var midway = await Store.LoadAsync(seed.Run, Token);
            if (failover) Assert.Equal(RunStatus.Aborting, midway.Snapshot.Status);
            else Assert.Contains(midway.Snapshot.Status, new[] { RunStatus.Aborting, RunStatus.Aborted });
            Assert.Equal(AbortSource.CLI, midway.Snapshot.Abort!.Source);
            if (failover)
            {
                Assert.Equal(ExecutionStatus.Running, midway.Snapshot.Tasks["node/task"]);
                second = StartWorker("second", false);
                await Wait(async () => (long)(await Scalar("""
                    SELECT count(*) FROM pg_stat_activity WHERE application_name='abort-recovery-second'
                    """))! > 0, second);
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync(Token);
            }
            await cli.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.Equal(0, cli.ExitCode);
            Assert.Empty(await started.Error);
            var lines = started.Lines.ToArray();
            Assert.Single(lines, line => line == $"Run {seed.Run:D}: Aborting");
            Assert.Single(lines, line => line == $"Run {seed.Run:D}: Aborted");
            var acceptance = Assert.Single(lines, line => line.StartsWith("Abort accepted: ", StringComparison.Ordinal));
            var accepted = JsonSerializer.Deserialize<JsonElement>(acceptance["Abort accepted: ".Length..]);
            Assert.Equal("CLI", accepted.GetProperty("source").GetString());
            Assert.Equal("Stop from actual CLI", accepted.GetProperty("reason").GetString());
            var final = await Store.LoadAsync(seed.Run, Token);
            Assert.Equal(RunStatus.Aborted, final.Snapshot.Status);
            Assert.Equal(midway.Snapshot.Abort, final.Snapshot.Abort);
            Assert.Equal(ExecutionStatus.Cancelled, final.Snapshot.Tasks["node/task"]);
            Assert.Equal(ExecutionStatus.Cancelled, final.Snapshot.Nodes["node"]);
            Assert.NotNull(final.Snapshot.EndedAt);
            Assert.Equal(accepted.GetProperty("by").GetString(), final.Snapshot.Abort!.By);
            Assert.Equal(accepted.GetProperty("at").GetDateTimeOffset(), final.Snapshot.Abort.At);
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1 AND kind=1 AND processed_at IS NOT NULL", seed.Run));
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1 AND action='run.abort'", seed.Run.ToString("D")));
            Assert.Equal(1L, await Scalar("""
                SELECT count(*) FROM run_events WHERE run_id=$1
                    AND convert_from(payload,'UTF8')::jsonb->'Abort' <> 'null'::jsonb
                """, seed.Run));
            Assert.Equal(failover ? 2L : 1L, await Scalar("SELECT epoch FROM leases WHERE name='engine'"));
        }
        finally
        {
            if (cli is not null) { await Stop(cli); cli.Dispose(); }
            await Stop(first);
            if (second is not null) { await Stop(second); second.Dispose(); }
        }
    }

    [Fact]
    public async Task AbortCli_actual_viewer_has_live_read_access_but_cannot_submit_abort()
    {
        var seed = await Seed(RegistryRole.Viewer);
        await using var server = await RunQueryServer.Start(database.DataSource, Token, abort: true, live: true);
        var started = StartCli(server, seed.Run, server.Jwt(seed.Group));
        using var cli = started.Process;
        try
        {
            await cli.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.True(cli.ExitCode == 1, await started.Error);
            Assert.Contains("HTTP 403", await started.Error);
            Assert.DoesNotContain(started.Lines, line => line.StartsWith("Abort accepted:", StringComparison.Ordinal));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1 AND kind=1", seed.Run));
            Assert.Null((await Store.LoadAsync(seed.Run, Token)).Snapshot.Abort);
        }
        finally { await Stop(cli); }
    }

    [Fact]
    public async Task AbortCli_lost_live_connection_is_explicit_failure_not_a_claim_that_durable_abort_was_undone()
    {
        var seed = await Seed();
        var server = await RunQueryServer.Start(database.DataSource, Token, abort: true, live: true);
        using var worker = StartWorker("first", true);
        Process? cli = null;
        var disposed = false;
        try
        {
            var started = StartCli(server, seed.Run, server.Jwt(seed.Group));
            cli = started.Process;
            await ObservedAborting(started);
            await server.DisposeAsync();
            disposed = true;
            await cli.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.Equal(2, cli.ExitCode);
            Assert.Contains("acceptance may already be durable", await started.Error);
            var stored = await Store.LoadAsync(seed.Run, Token);
            Assert.Equal(RunStatus.Aborting, stored.Snapshot.Status);
            Assert.Equal(AbortSource.CLI, stored.Snapshot.Abort!.Source);
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1 AND kind=1", seed.Run));
        }
        finally
        {
            if (!disposed) await server.DisposeAsync();
            if (cli is not null) { await Stop(cli); cli.Dispose(); }
            await Stop(worker);
        }
    }

    private async Task<(Guid Group, Guid Run)> Seed(RegistryRole role = RegistryRole.Operator)
    {
        await Execute("TRUNCATE commands,leases RESTART IDENTITY");
        var team = "abortcli-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team, [new(team, "release", "description", new([]), [new RegistryRoleBinding(role, group)])]), Token);
        await transaction.CommitAsync(Token);
        var run = new RunState(Guid.CreateVersion7(), [new NodeDefinition("node", [], [new TaskDefinition("task")])],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        var store = Store;
        await store.CreateAsync(run, team + "/release/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await store.AppendAsync(run, EventEntity.Node, "node", (int)state, DateTimeOffset.UtcNow, Token);
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await store.AppendAsync(run, EventEntity.Task, "node/task", (int)state, DateTimeOffset.UtcNow, Token);
        return (group, run.Snapshot.RunId);
    }

    private Process StartWorker(string holder, bool hold)
    {
        var start = Start(typeof(FixtureMarker).Assembly.Location);
        start.Environment["WF_RECOVERY_DATABASE"] = new NpgsqlConnectionStringBuilder(database.WorkerConnectionString)
            { ApplicationName = "abort-recovery-" + holder }.ConnectionString;
        start.Environment["WF_RECOVERY_HOLDER"] = "abort-" + holder;
        start.Environment["WF_RECOVERY_HOLD"] = hold ? "true" : "false";
        start.Environment["WF_RECOVERY_ABORT"] = "true";
        return Process.Start(start) ?? throw new InvalidOperationException("Abort recovery process did not start.");
    }

    private static (Process Process, ConcurrentQueue<string> Lines, Task Aborting, Task<string> Error) StartCli(
        RunQueryServer server, Guid run, string jwt)
    {
        var start = Start(typeof(Wf.Cli.Program).Assembly.Location);
        foreach (var argument in new[] { "abort", run.ToString("D"), "--reason", "Stop from actual CLI" })
            start.ArgumentList.Add(argument);
        start.Environment["WF_ENGINE_URL"] = server.Origin.AbsoluteUri;
        start.Environment["WF_ACCESS_TOKEN"] = jwt;
        var lines = new ConcurrentQueue<string>();
        var aborting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, message) =>
        {
            if (message.Data is not { } line) return;
            lines.Enqueue(line);
            if (line == $"Run {run:D}: Aborting") aborting.TrySetResult();
        };
        if (!process.Start()) throw new InvalidOperationException("Abort CLI did not start.");
        process.BeginOutputReadLine();
        return (process, lines, aborting.Task, process.StandardError.ReadToEndAsync(Token));
    }

    private static ProcessStartInfo Start(string entry)
    {
        var assembly = typeof(AbortTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), entry })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task ObservedAborting((Process Process, ConcurrentQueue<string> Lines, Task Aborting, Task<string> Error) started)
    {
        var completed = await Task.WhenAny(started.Aborting, started.Process.WaitForExitAsync(Token))
            .WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.True(completed == started.Aborting,
            started.Process.HasExited ? $"CLI exited {started.Process.ExitCode}: {await started.Error}" : "No Aborting event was observed.");
    }

    private static async Task Stop(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static async Task Wait(Func<Task<bool>> condition, Process worker)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.False(worker.HasExited, worker.HasExited ? await worker.StandardError.ReadToEndAsync(Token) : "");
            Assert.True(DateTimeOffset.UtcNow < deadline, "The actual recovery process did not become ready.");
            await Task.Delay(25, Token);
        }
    }

    private async Task<object?> Scalar(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        return await query.ExecuteScalarAsync(Token);
    }

    private async Task Execute(string sql)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        await query.ExecuteNonQueryAsync(Token);
    }
}

public sealed class AbortDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
    public string WorkerConnectionString => DatabaseConnectionString;
}
