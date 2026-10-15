using System.Collections.Immutable;
using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Recovery;

public sealed class AgentLossSweeperTests(AgentLossDatabase database) : IClassFixture<AgentLossDatabase>
{
    private readonly LossClock clock = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance);

    [Fact]
    public async Task Attempts_remain_reclaimable_through_twenty_nine_seconds_then_are_lost_at_thirty()
    {
        await Reset();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await Running(session);
        using var sweeper = Sweeper(registry);
        foreach (var seconds in new[] { 14, 15, 29 })
        {
            clock.SetAge(seconds);
            Assert.Empty(await sweeper.SweepAsync(Token));
            Assert.Equal(seconds < 15 ? AgentStatus.Healthy : AgentStatus.Unhealthy, registry.Find(session.AgentId)!.Status);
            Assert.Equal((short)ExecutionStatus.Running, await AttemptStatus(run, "task"));
        }
        clock.SetAge(30);
        var lost = Assert.Single(await sweeper.SweepAsync(Token));
        Assert.Equal(run.Snapshot.RunId, lost.RunId);
        Assert.Equal("node/task", lost.TaskKey);
        Assert.Equal(clock.GetUtcNow(), lost.LostAt);
        Assert.Equal((short)ExecutionStatus.Failed, await AttemptStatus(run, "task"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM dispatch_outbox"));
        Assert.Empty(await sweeper.SweepAsync(Token));
        Assert.Equal(ExecutionStatus.Running, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["node/task"]);
        Assert.Equal((short)FailureCategory.AgentLost, await Scalar("SELECT failure_category FROM task_attempts"));
    }

    [Fact]
    public async Task Engine_restart_uses_original_last_heartbeat_without_another_thirty_second_grace()
    {
        await Reset();
        Guid agentId;
        using (var old = new AgentRegistry(database.DataSource, clock))
        {
            var session = await old.RegisterAsync(Hello(), Token);
            agentId = session.AgentId;
            await Running(session);
        }
        clock.SetAge(31);
        using var restored = new AgentRegistry(database.DataSource, clock);
        await restored.LoadAsync(Token);
        using var sweeper = Sweeper(restored);
        Assert.Single(await sweeper.SweepAsync(Token));
        Assert.Equal(AgentStatus.Unhealthy, restored.Find(agentId)!.Status);
    }

    [Fact]
    public async Task Reconnected_hello_preserves_reported_attempts_and_loses_omitted_attempts_immediately()
    {
        await Reset();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var hello = Hello();
        var old = await registry.RegisterAsync(hello, Token);
        var run = await Running(old, ["task", "omitted"]);
        clock.SetAge(20);
        hello.Inflight.Add(Reference(run, "task"));
        var session = await registry.RegisterAsync(hello, Token);
        using var sweeper = Sweeper(registry);
        var lost = Assert.Single(await sweeper.ReconcileAsync(session, hello.Inflight, Token));
        Assert.Equal("node/omitted", lost.TaskKey);
        Assert.Equal((short)ExecutionStatus.Running, await AttemptStatus(run, "task"));
        Assert.Equal((short)ExecutionStatus.Failed, await AttemptStatus(run, "omitted"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sweeper.ReconcileAsync(old, [], Token));
        Assert.Empty(await sweeper.SweepAsync(Token));
        clock.SetAge(49);
        Assert.Empty(await sweeper.SweepAsync(Token));
        clock.SetAge(50);
        Assert.Equal("node/task", Assert.Single(await sweeper.SweepAsync(Token)).TaskKey);
    }

    [Fact]
    public async Task Inflight_matching_uses_run_task_and_attempt_not_only_task_name()
    {
        await Reset();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var hello = Hello();
        var session = await registry.RegisterAsync(hello, Token);
        var run = await Running(session);
        var wrongAttempt = Reference(run, "task");
        wrongAttempt.Attempt = 2;
        using var sweeper = Sweeper(registry);
        Assert.Single(await sweeper.ReconcileAsync(session, [wrongAttempt], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => sweeper.ReconcileAsync(session, [wrongAttempt, wrongAttempt], Token));
    }

    [Fact]
    public async Task Heartbeat_refreshes_reclaim_deadline_and_terminal_attempts_are_never_reclassified()
    {
        await Reset();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await Running(session, ["task", "done"]);
        await Execute("UPDATE task_attempts SET status=5 WHERE task_key='node/done'");
        clock.SetAge(29);
        Assert.True(await registry.HeartbeatAsync(session, new Heartbeat {
            At = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(clock.GetUtcNow())
        }, Token));
        using var sweeper = Sweeper(registry);
        clock.SetAge(30);
        Assert.Empty(await sweeper.SweepAsync(Token));
        clock.SetAge(59);
        Assert.Single(await sweeper.SweepAsync(Token));
        Assert.Equal((short)ExecutionStatus.Succeeded, await AttemptStatus(run, "done"));
    }

    [Fact]
    public async Task Persistence_failure_rolls_back_attempt_classification_and_outbox_removal()
    {
        await Reset();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await Running(session);
        await Execute("""
            CREATE FUNCTION reject_loss_delete() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected outbox failure'; END $$;
            CREATE TRIGGER reject_loss BEFORE DELETE ON dispatch_outbox FOR EACH ROW EXECUTE FUNCTION reject_loss_delete()
            """);
        using var sweeper = Sweeper(registry);
        try { await Assert.ThrowsAsync<PostgresException>(() => sweeper.ReconcileAsync(session, [], Token)); }
        finally { await Execute("DROP TRIGGER reject_loss ON dispatch_outbox; DROP FUNCTION reject_loss_delete()"); }
        Assert.Equal((short)ExecutionStatus.Running, await AttemptStatus(run, "task"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM dispatch_outbox"));
        Assert.Single(await sweeper.ReconcileAsync(session, [], Token));
    }

    [Fact]
    public async Task Stale_leader_cannot_classify_attempts_after_takeover()
    {
        await Reset();
        using var first = Manager();
        using var second = Manager();
        Assert.True(await first.TryAcquireOrRenewAsync(Token));
        using var registry = new AgentRegistry(database.DataSource, clock, first);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await Running(session);
        using var sweeper = Sweeper(registry, first);
        await Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.True(await second.TryAcquireOrRenewAsync(Token));
        await Assert.ThrowsAsync<NotLeaderException>(() => sweeper.ReconcileAsync(session, [], Token));
        Assert.Equal((short)ExecutionStatus.Running, await AttemptStatus(run, "task"));
    }

    [Fact]
    public async Task Real_gateway_acknowledges_hello_only_after_omitted_attempt_classification_commits()
    {
        await Reset();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var hello = Hello();
        var old = await registry.RegisterAsync(hello, Token);
        var run = await Running(old);
        using var sweeper = Sweeper(registry);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(Store);
        builder.Services.AddSingleton(sweeper);
        await using var app = builder.Build();
        app.MapGrpcService<AgentGatewayService>();
        await app.StartAsync(Token);
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var channel = GrpcChannel.ForAddress(address);
            using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: Token);
            await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Hello = hello }, Token);
            Assert.True(await call.ResponseStream.MoveNext(Token));
            Assert.Equal(1UL, call.ResponseStream.Current.Ack.Sequence);
            Assert.Equal((short)ExecutionStatus.Failed, await AttemptStatus(run, "task"));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM dispatch_outbox"));
        }
        finally { await app.StopAsync(Token); }
    }

    private AgentLossSweeper Sweeper(AgentRegistry registry, LeaseManager? manager = null) =>
        new(database.DataSource, registry, clock, NullLogger<AgentLossSweeper>.Instance, manager);
    private LeaseManager Manager() => new(database.DataSource,
        new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5400")), NullLogger<LeaseManager>.Instance);
    private async Task<RunState> Running(AgentSession session, string[]? taskKeys = null)
    {
        var run = new RunState(Guid.CreateVersion7(), [new NodeDefinition("node", [],
                (taskKeys ?? ["task"]).Select(key => new TaskDefinition(key)).ToImmutableArray())],
            new WorkflowMetadata("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await Store.CreateAsync(run, "tests/agent-loss", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        await Store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, clock.GetUtcNow(), Token);
        foreach (var key in run.Snapshot.Tasks.Keys.ToArray())
        {
            foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                await Store.AppendAsync(run, EventEntity.Task, key, (int)status, clock.GetUtcNow(), Token);
            await Execute("""
                WITH attempt AS (
                    INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,deadline_at)
                    VALUES(uuid_v7_run_day($1),$1,$2,1,$3,3,$4) RETURNING *
                )
                INSERT INTO dispatch_outbox(run_day,run_id,task_key,attempt,agent_id,payload)
                SELECT run_day,run_id,task_key,attempt,agent_id,''::bytea FROM attempt
                """, run.Snapshot.RunId, key, session.AgentId, clock.GetUtcNow().AddHours(1));
        }
        return run;
    }
    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new() { Id = id.ToString(), Name = $"loss-{id:N}", Os = "linux", Arch = "x64",
            Cores = 4, RamMb = 8192, MaxTasks = 4, Pool = "test", Version = "1.0", ProtocolVersion = 1 };
    }
    private static AttemptRef Reference(RunState run, string key) => new() {
        RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "node", TaskKey = key, Attempt = 1
    };
    private async Task Reset() => await Execute("TRUNCATE agents,runs CASCADE; DELETE FROM leases");
    private async Task<object?> AttemptStatus(RunState run, string task) =>
        await Scalar("SELECT status FROM task_attempts WHERE run_id=$1 AND task_key=$2", run.Snapshot.RunId, $"node/{task}");
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
    private sealed class LossClock : TimeProvider
    {
        private readonly DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        private int age;
        public override DateTimeOffset GetUtcNow() => start.AddSeconds(age);
        public void SetAge(int seconds) => age = seconds;
    }
}

public sealed class AgentLossDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql", "0009_leases.sql"];
}
