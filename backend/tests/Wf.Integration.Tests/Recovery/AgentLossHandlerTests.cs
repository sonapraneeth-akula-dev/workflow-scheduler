using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;
using Wf.TaskHost.Fixture;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Recovery;

public sealed class AgentLossHandlerTests(ReplacementDatabase database, ITestOutputHelper output) : IClassFixture<ReplacementDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly ReplacementClock clock = new();

    [Fact]
    public async Task Two_platform_replacements_do_not_consume_author_retries_and_third_loss_fails()
    {
        await using var fixture = await Setup(new(TimeSpan.FromMinutes(10),
            new RetryPolicy(9, on: [FailureCategory.AgentLost])));
        var session = fixture.First;
        for (short attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Single(await fixture.Sweeper.ReconcileAsync(session, [], Token));
            await fixture.Registry.RegisterAsync(Hello(), Token);
            var disposition = await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", attempt, Token);
            Assert.Equal(attempt <= 2 ? AgentLossDisposition.Replaced : AgentLossDisposition.Failed, disposition);
            if (attempt <= 2)
            {
                var id = Assert.IsType<Guid>(await Scalar($"SELECT agent_id FROM task_attempts WHERE attempt={attempt + 1}"));
                session = new(id, fixture.Registry.Find(id)!.SessionId);
            }
        }
        Assert.Equal(3L, await Scalar("SELECT count(*) FROM task_attempts"));
        Assert.Equal(0, Convert.ToInt32(await Scalar("SELECT sum(author_retries) FROM task_attempts")));
        Assert.Equal(2, Convert.ToInt32(await Scalar("SELECT max(platform_retries) FROM task_attempts")));
        Assert.Equal(ExecutionStatus.Failed, (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.Tasks["node/task"]);
        Assert.Contains("two platform retries", Assert.IsType<string>(await Scalar("SELECT error FROM task_attempts WHERE attempt=3")));
        Assert.Equal(AgentLossDisposition.AlreadyHandled,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 3, Token));
    }

    [Fact]
    public async Task NoAutoRetry_survives_dispatcher_reconstruction_even_when_author_policy_includes_agent_loss()
    {
        await using var fixture = await Setup(new(TimeSpan.FromMinutes(10),
            new RetryPolicy(9, on: [FailureCategory.AgentLost]), autoRetry: false));
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        await fixture.Registry.RegisterAsync(Hello(), Token);
        using var rebuilt = new Dispatcher(database.DataSource, fixture.Store, fixture.Registry, new PlacementEngine(), clock);
        var handler = new AgentLossHandler(database.DataSource, rebuilt, fixture.Leader, clock, NullLogger<AgentLossHandler>.Instance);
        using (handler)
        {
            Assert.Equal(1, await handler.ProcessAsync(Token));
            Assert.Equal(0, await handler.ProcessAsync(Token));
        }
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM task_attempts"));
        Assert.Contains("NoAutoRetry", Assert.IsType<string>(await Scalar("SELECT error FROM task_attempts")));
        Assert.Equal(ExecutionStatus.Failed, (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.Tasks["node/task"]);
    }

    [Fact]
    public async Task No_eligible_other_agent_keeps_work_pending_without_spending_budget_or_advancing_events()
    {
        await using var fixture = await Setup();
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        var before = (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.LastSequence;
        Assert.Equal(AgentLossDisposition.WaitingForMachine,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        Assert.Equal(before, (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(0, Convert.ToInt32(await Scalar("SELECT platform_retries FROM task_attempts")));
        var next = await fixture.Registry.RegisterAsync(Hello(), Token);
        Assert.Equal(AgentLossDisposition.Replaced,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        Assert.Equal(next.AgentId, await Scalar("SELECT agent_id FROM task_attempts WHERE attempt=2"));
        Assert.Equal(AgentLossDisposition.AlreadyHandled,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 1, Token));
    }

    [Fact]
    public async Task Replacement_rolls_back_retry_events_budget_and_outbox_on_persistence_failure()
    {
        await using var fixture = await Setup();
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        await fixture.Registry.RegisterAsync(Hello(), Token);
        var before = (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.LastSequence;
        var revision = fixture.Placement.Snapshot.Revision;
        await Execute("ALTER TABLE task_attempts ADD CONSTRAINT reject_replacement CHECK(attempt<2)");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => fixture.Dispatcher.ReplaceLostAsync(
                fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        }
        finally { await Execute("ALTER TABLE task_attempts DROP CONSTRAINT reject_replacement"); }
        Assert.Equal(before, (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(revision, fixture.Placement.Snapshot.Revision);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM task_attempts"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM dispatch_outbox"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM task_attempts WHERE replacement_processed_at IS NOT NULL"));
        Assert.Equal(AgentLossDisposition.Replaced,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 1, Token));
    }

    [Fact]
    public async Task Persisted_attempt_timeout_is_reset_but_never_exceeds_run_or_node_deadline()
    {
        await using var fixture = await Setup(new(TimeSpan.FromMinutes(10), new()));
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        await fixture.Registry.RegisterAsync(Hello(), Token);
        var deadline = clock.GetUtcNow().AddMinutes(2);
        await Execute("UPDATE node_runs SET deadline_at=$1", deadline);
        Assert.Equal(AgentLossDisposition.Replaced,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        await using var command = database.DataSource.CreateCommand("SELECT deadline_at FROM task_attempts WHERE attempt=2");
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(deadline, reader.GetFieldValue<DateTimeOffset>(0));
    }

    [Fact]
    public async Task Expired_node_deadline_and_legacy_missing_recovery_data_fail_explicitly()
    {
        await using var fixture = await Setup();
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        await fixture.Registry.RegisterAsync(Hello(), Token);
        await Execute("UPDATE node_runs SET deadline_at=$1", clock.GetUtcNow().AddSeconds(-1));
        Assert.Equal(AgentLossDisposition.Failed,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        Assert.Contains("deadline", Assert.IsType<string>(await Scalar("SELECT error FROM task_attempts")));
    }

    [Fact]
    public async Task Legacy_attempts_without_recovery_context_are_not_reexecuted_with_invented_defaults()
    {
        await using var fixture = await Setup();
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        await Execute("UPDATE task_attempts SET dispatch_payload=NULL,placement_request=NULL,execution_policy=NULL");
        Assert.Equal(AgentLossDisposition.Failed,
            await fixture.Dispatcher.ReplaceLostAsync(fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        Assert.Contains("unsafe", Assert.IsType<string>(await Scalar("SELECT error FROM task_attempts")));
    }

    [Fact]
    public async Task Corrupt_recovery_identity_is_rejected_without_dispatching_a_different_run()
    {
        await using var fixture = await Setup();
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        await fixture.Registry.RegisterAsync(Hello(), Token);
        await Execute("UPDATE task_attempts SET placement_request=jsonb_set(placement_request,'{RunId}',to_jsonb($1::text))",
            Guid.CreateVersion7().ToString("D"));
        var sequence = (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.LastSequence;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Dispatcher.ReplaceLostAsync(
            fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        Assert.Equal(sequence, (await fixture.Store.LoadAsync(fixture.Run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM task_attempts"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM dispatch_outbox"));
    }

    [Fact]
    public async Task Stale_leader_cannot_reexecute_lost_work_after_takeover()
    {
        await using var fixture = await Setup();
        await fixture.Sweeper.ReconcileAsync(fixture.First, [], Token);
        await fixture.Registry.RegisterAsync(Hello(), Token);
        using var nextLeader = Manager();
        await Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.True(await nextLeader.TryAcquireOrRenewAsync(Token));
        await Assert.ThrowsAsync<NotLeaderException>(() => fixture.Dispatcher.ReplaceLostAsync(
            fixture.Run.Snapshot.RunId, "node/task", 1, Token));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM task_attempts"));
    }

    [Fact]
    public Task Killed_agent_task_is_replaced_on_second_real_agent_after_virtual_thirty_second_grace() =>
        KilledAgentReplacement(clock, wallClock: false);

    [Fact]
    public Task Killed_agent_tree_starts_replacement_within_thirty_real_seconds_after_full_reclaim_expiry() =>
        KilledAgentReplacement(TimeProvider.System, wallClock: true);

    private async Task KilledAgentReplacement(TimeProvider time, bool wallClock)
    {
        await Reset();
        using var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        await leader.StartAsync(Token);
        using var registry = new AgentRegistry(database.DataSource, time, leader);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
        using var placement = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), time);
        using var sweeper = new AgentLossSweeper(database.DataSource, registry, time, NullLogger<AgentLossSweeper>.Instance, leader);
        using var handler = new AgentLossHandler(database.DataSource, placement, leader, time, NullLogger<AgentLossHandler>.Instance);
        var bytes = await Package();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "replacement-tests", Guid.NewGuid().ToString("N")));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http1));
        // gRPC uses a separate cleartext HTTP/2 listener; the package listener remains HTTP/1.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(leader);
        builder.Services.AddSingleton(sweeper);
        builder.Services.AddSingleton(new Wf.ControlPlane.Engine.ResourceSampleSink(database.DataSource, store,
            new Wf.ControlPlane.Storage.FileSystemBlobStore(Path.Combine(root, "resources"), RandomNumberGenerator.GetBytes(32))));
        if (wallClock)
        {
            builder.Services.AddHostedService(_ => sweeper);
            builder.Services.AddHostedService(_ => handler);
        }
        await using var app = builder.Build();
        app.MapGrpcService<AgentGatewayService>();
        app.MapGet("/package.zip", () => Microsoft.AspNetCore.Http.Results.Bytes(bytes, "application/zip"));
        await app.StartAsync(Token);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        var packageUri = new Uri(new Uri(addresses[0]), "package.zip");
        var gatewayUri = new Uri(addresses[1]);
        Directory.CreateDirectory(root);
        Process? first = null, second = null;
        try
        {
            var firstId = Guid.NewGuid();
            first = StartAgent(firstId, true);
            await Wait(() => Task.FromResult(registry.Find(firstId) is not null), first);
            var run = await ReadyRun(store, "work", "agentloss", time);
            await placement.DispatchAsync(run, new(run.Snapshot.RunId, "work", "agentloss", 1, sha),
                new() { Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = "agentloss", Attempt = 1 },
                    Version = "1.0.0", Sha256 = sha, PackageUri = packageUri.ToString(),
                    DeadlineAt = Timestamp.FromDateTimeOffset(time.GetUtcNow().AddMinutes(5)) }, Token);
            await Wait(async () => (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/agentloss"] == ExecutionStatus.Running, first);
            var secondId = Guid.NewGuid();
            if (wallClock)
            {
                second = StartAgent(secondId, false);
                await Wait(() => Task.FromResult(registry.Find(secondId) is not null), second);
                var registeredAt = registry.Find(secondId)!.LastHeartbeatAt;
                await Wait(() => Task.FromResult(registry.Find(secondId)!.LastHeartbeatAt > registeredAt), second);
                Assert.Contains(registry.DispatchableAgents(), agent => agent.Id == secondId && agent.SlotsUsed == 0);
                Assert.Equal((short)ExecutionStatus.Running, await Scalar("SELECT status FROM task_attempts WHERE attempt=1"));
                output.WriteLine($"Spare registered={registeredAt:O}; heartbeat={registry.Find(secondId)!.LastHeartbeatAt:O}; id={secondId}; PID={second.Id}");
            }
            var killedAt = time.GetUtcNow();
            var killedPid = first.Id;
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(Token);
            if (wallClock)
            {
                var lastHeartbeat = new DateTimeOffset(Assert.IsType<DateTime>(await Scalar(
                    "SELECT last_heartbeat_at FROM agents WHERE id=$1", firstId)));
                var expiry = lastHeartbeat.AddSeconds(30);
                var startDeadline = expiry.AddSeconds(30);
                output.WriteLine($"Killed process tree PID={killedPid}; kill={killedAt:O}; lastHeartbeat={lastHeartbeat:O}; reclaimExpiry={expiry:O}; replacementStartDeadline={startDeadline:O}");
                await Wait(async () => await Scalar("SELECT started_at FROM task_attempts WHERE attempt=2") is DateTime,
                    second!, startDeadline - time.GetUtcNow(), "Replacement did not durably enter agent execution within 30 seconds after full reclaim expiry.");
                await Wait(async () => (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/agentloss"] == ExecutionStatus.Succeeded,
                    second!, startDeadline - time.GetUtcNow(), "Short fixture success did not prove TaskHost/SDK startup within 30 seconds after reclaim expiry.");
                await VerifyWallClockReplacement(firstId, secondId, lastHeartbeat, expiry);
                Assert.True(leader.IsLeader, "Healthy leader is a precondition for the replacement-start gate.");
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                Assert.Single(await sweeper.SweepAsync(Token));
                second = StartAgent(secondId, false);
                await Wait(() => Task.FromResult(registry.Find(secondId) is not null), second);
                var elapsed = Stopwatch.StartNew();
                Assert.Equal(1, await handler.ProcessAsync(Token));
                await Wait(async () => (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/agentloss"] == ExecutionStatus.Succeeded, second);
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), elapsed.Elapsed.ToString());
            }
            Assert.Equal(secondId, await Scalar("SELECT agent_id FROM task_attempts WHERE attempt=2"));
            Assert.Equal(1, Convert.ToInt32(await Scalar("SELECT platform_retries FROM task_attempts WHERE attempt=2")));
            Assert.Equal(0, Convert.ToInt32(await Scalar("SELECT author_retries FROM task_attempts WHERE attempt=2")));
        }
        finally
        {
            if (first is not null) { await Stop(first); first.Dispose(); }
            if (second is not null) { await Stop(second); second.Dispose(); }
            await app.StopAsync(Token);
            await leader.StopAsync(Token);
            Directory.Delete(root, recursive: true);
        }

        Process StartAgent(Guid id, bool hold)
        {
            var assembly = typeof(AgentLossHandlerTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(AgentConnection).Assembly.Location })
                start.ArgumentList.Add(argument);
            var directory = Path.Combine(root, id.ToString("N"));
            Directory.CreateDirectory(directory);
            start.WorkingDirectory = directory;
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["Agent__Id"] = id.ToString("D");
            start.Environment["Agent__Name"] = $"replacement-{id:N}";
            start.Environment["Agent__Engines__0"] = gatewayUri.ToString();
            start.Environment["Agent__MaxTasks"] = "1";
            start.Environment["WF_FIXTURE_AGENT_LOSS_HOLD"] = hold ? "true" : "false";
            var process = Process.Start(start) ?? throw new InvalidOperationException("Agent did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
    }

    private async Task VerifyWallClockReplacement(Guid firstId, Guid secondId, DateTimeOffset lastHeartbeat, DateTimeOffset expiry)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT a.last_heartbeat_at,l.ended_at,l.failure_category,r.started_at,r.ended_at,r.deadline_at,
                   COALESCE(n.deadline_at,run.deadline_at),run.deadline_at,r.status,r.agent_id,a2.last_heartbeat_at
            FROM task_attempts l JOIN agents a ON a.id=l.agent_id
            JOIN task_attempts r ON r.run_id=l.run_id AND r.task_key=l.task_key AND r.attempt=2
            JOIN agents a2 ON a2.id=r.agent_id
            JOIN runs run ON run.id=r.run_id
            JOIN node_runs n ON n.run_id=r.run_id AND n.node_key='work'
            WHERE l.attempt=1 AND a.id=$1
            """);
        command.Parameters.AddWithValue(firstId);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        var classifiedAt = reader.GetFieldValue<DateTimeOffset>(1);
        var startedAt = reader.GetFieldValue<DateTimeOffset>(3);
        var succeededAt = reader.GetFieldValue<DateTimeOffset>(4);
        output.WriteLine($"Classification={classifiedAt:O}; replacementAgentExecutionEntry(pre-host)={startedAt:O}; replacementSucceeded={succeededAt:O}; spareLastHeartbeat={reader.GetFieldValue<DateTimeOffset>(10):O}");
        output.WriteLine($"LastHeartbeat->classification={(classifiedAt - lastHeartbeat).TotalSeconds:F6}s; reclaimExpiry->agentExecutionEntry(pre-host)={(startedAt - expiry).TotalSeconds:F6}s; agentExecutionEntry->success={(succeededAt - startedAt).TotalSeconds:F6}s; reclaimExpiry->TaskHost/SDKStartUpperBound(success)={(succeededAt - expiry).TotalSeconds:F6}s");
        Assert.Equal(lastHeartbeat, reader.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal((short)FailureCategory.AgentLost, reader.GetInt16(2));
        Assert.True(classifiedAt >= expiry, "Loss must not be classified before the full 30-second last-heartbeat reclaim grace.");
        Assert.True(startedAt >= classifiedAt);
        Assert.True(startedAt <= expiry.AddSeconds(30), "Pre-host agent execution entry exceeded 30 seconds after reclaim expiry.");
        // Short fixture success conservatively bounds TaskHost/SDK entry, which the pre-host TaskStarted event cannot prove.
        Assert.True(succeededAt <= expiry.AddSeconds(30),
            "Short fixture success must prove TaskHost/SDK startup within the post-reclaim start budget; it is not an exact SDK-entry timestamp.");
        Assert.True(succeededAt >= startedAt && succeededAt <= startedAt.AddSeconds(30),
            "Completion has a separate 30-second fixture budget, not a 30-second total recovery promise.");
        foreach (var column in new[] { 5, 6, 7 }) Assert.True(succeededAt <= reader.GetFieldValue<DateTimeOffset>(column));
        Assert.Equal((short)ExecutionStatus.Succeeded, reader.GetInt16(8));
        Assert.Equal(secondId, reader.GetGuid(9));
        var spareHeartbeat = reader.GetFieldValue<DateTimeOffset>(10);
        Assert.True(spareHeartbeat > lastHeartbeat && succeededAt - spareHeartbeat < AgentRegistry.HeartbeatTimeout,
            "Spare must keep sending real heartbeats and remain healthy during reclaim and replacement.");
        Assert.False(await reader.ReadAsync(Token));
    }

    private async Task<Fixture> Setup(ExecutionPolicy? policy = null)
    {
        await Reset();
        var leader = Manager();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var registry = new AgentRegistry(database.DataSource, clock, leader);
        var first = await registry.RegisterAsync(Hello(), Token);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
        var placement = new PlacementEngine();
        var dispatcher = new Dispatcher(database.DataSource, store, registry, placement, clock);
        var run = await ReadyRun(store);
        var command = new DispatchCommand { Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "node", TaskKey = "task", Attempt = 1 },
            Version = "1.0.0", Sha256 = new string('a', 64), PackageUri = "http://localhost/package.zip",
            DeadlineAt = Timestamp.FromDateTimeOffset(clock.GetUtcNow().AddHours(1)) };
        await dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, "node", "task", 1, command.Sha256),
            command, policy ?? ExecutionPolicy.Default, Token);
        await store.AcceptAsync(first, new AgentMessage { TaskStarted = new() {
            Task = command.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.GetUtcNow())
        } }, Token);
        return new(leader, registry, store, placement, dispatcher, first, run,
            new(database.DataSource, registry, clock, NullLogger<AgentLossSweeper>.Instance, leader));
    }

    private async Task<RunState> ReadyRun(RunStore store, string node = "node", string task = "task", TimeProvider? time = null)
    {
        var now = (time ?? clock).GetUtcNow();
        var run = new RunState(Guid.CreateVersion7(), [new(node, [], [new(task)])],
            new WorkflowMetadata("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, "tests/replacement", Guid.CreateVersion7(), now.AddHours(1), Token);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, now, Token);
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await store.AppendAsync(run, EventEntity.Node, node, (int)state, now, Token);
        await store.AppendAsync(run, EventEntity.Task, $"{node}/{task}", (int)ExecutionStatus.Ready, now, Token);
        return run;
    }
    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new() { Id = id.ToString(), Name = $"replace-{id:N}", Os = "linux", Arch = "x64",
            Cores = 4, RamMb = 8192, MaxTasks = 4, Pool = "test", Version = "1.0", ProtocolVersion = 1 };
    }
    private LeaseManager Manager() => new(database.DataSource,
        new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5500")), NullLogger<LeaseManager>.Instance);
    private async Task Reset() => await Execute("TRUNCATE agents,runs CASCADE; DELETE FROM leases");
    private async Task Execute(string sql, params object[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await command.ExecuteNonQueryAsync(Token);
    }
    private async Task<object?> Scalar(string sql, params object[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        return await command.ExecuteScalarAsync(Token);
    }
    private async Task<byte[]> Package()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                await using var input = File.OpenRead(path);
                await using var output = zip.CreateEntry(Path.GetFileName(path)).Open();
                await input.CopyToAsync(output, Token);
            }
            await using var writer = new StreamWriter(zip.CreateEntry("wf-package.json").Open(), Encoding.UTF8);
            await writer.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""");
        }
        return bytes.ToArray();
    }
    private async Task Wait(Func<Task<bool>> condition, Process process, TimeSpan? timeout = null, string? failure = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.False(process.HasExited, "Agent process exited before the expected state.");
            Assert.True(elapsed.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)), failure ?? "Timed out waiting for agent state.");
            await Task.Delay(10, Token);
        }
    }
    private static async Task Stop(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    private sealed class ReplacementClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private sealed record Fixture(LeaseManager Leader, AgentRegistry Registry, RunStore Store, PlacementEngine Placement,
        Dispatcher Dispatcher, AgentSession First, RunState Run, AgentLossSweeper Sweeper) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Sweeper.Dispose();
            Dispatcher.Dispose();
            Registry.Dispose();
            Leader.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class ReplacementDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql",
            "0009_leases.sql", "0011_attempt_replacement.sql", "0025_resource_samples.sql"];
}
