using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Journal;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Leadership;

public sealed class LeaseTests(LeaseDatabase database) : IClassFixture<LeaseDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LeaseConcurrentAcquisitionHasOneWinnerAndRenewalPreservesEpoch()
    {
        await Reset();
        using var first = Manager(5100);
        using var second = Manager(5101);
        var results = await Task.WhenAll(first.TryAcquireOrRenewAsync(Token), second.TryAcquireOrRenewAsync(Token));
        Assert.Equal(1, results.Count(result => result));
        var leader = first.IsLeader ? first : second;
        var standby = first.IsLeader ? second : first;
        var epoch = leader.RequireLease().Epoch;
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        Assert.Equal(epoch, leader.RequireLease().Epoch);
        Assert.False(await standby.TryAcquireOrRenewAsync(Token));
        Assert.Equal(leader.LeaderHint, standby.LeaderHint);
        await leader.ReleaseAsync(Token);
        Assert.False(leader.IsLeader);
        Assert.True(await standby.TryAcquireOrRenewAsync(Token));
        Assert.Equal(epoch + 1, standby.RequireLease().Epoch);
    }

    [Fact]
    public async Task LeaseBackgroundRenewsEveryThreeSecondsWithTenSecondTtl()
    {
        await Reset();
        using var leader = Manager(5100);
        using var standby = Manager(5101);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var epoch = leader.RequireLease().Epoch;
        try
        {
            await leader.StartAsync(Token);
            await standby.StartAsync(Token);
            await Task.Delay(TimeSpan.FromSeconds(7), Token);
            Assert.Equal(epoch, leader.RequireLease().Epoch);
            Assert.False(standby.IsLeader);
            await using var remaining = database.DataSource.CreateCommand(
                "SELECT expires_at-clock_timestamp() FROM leases WHERE name='engine'");
            Assert.InRange(Assert.IsType<TimeSpan>(await remaining.ExecuteScalarAsync(Token)),
                TimeSpan.FromSeconds(6), LeaseManager.TimeToLive);
        }
        finally
        {
            await standby.StopAsync(Token);
            await leader.StopAsync(Token);
        }
    }

    [Fact]
    public async Task LeaseStandbyAcquiresAtTenSecondExpiryWithoutWaitingForAnotherRenewalPeriod()
    {
        await Reset();
        using var paused = Manager(5100);
        using var standby = Manager(5101);
        Assert.True(await paused.TryAcquireOrRenewAsync(Token));
        var epoch = paused.RequireLease().Epoch;
        var elapsed = Stopwatch.StartNew();
        try
        {
            await standby.StartAsync(Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(11));
            while (!standby.IsLeader) await Task.Delay(5, timeout.Token);
            Assert.Equal(epoch + 1, standby.RequireLease().Epoch);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10.5), elapsed.Elapsed.ToString());
            Assert.False(paused.IsLeader);
        }
        finally { await standby.StopAsync(Token); }
    }

    [Fact]
    public async Task LeaseExpiryBetweenConflictAndHintRetriesWithoutAnotherThreeSecondDelay()
    {
        await Reset();
        using var paused = Manager(5100);
        using var standby = Manager(5101);
        Assert.True(await paused.TryAcquireOrRenewAsync(Token));
        await using (var setup = database.DataSource.CreateCommand("""
            CREATE TABLE lease_expiry_target(holder text PRIMARY KEY);
            CREATE FUNCTION expire_lease_after_conflict() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                UPDATE leases SET expires_at=clock_timestamp()-interval '1 second'
                WHERE holder IN (SELECT holder FROM lease_expiry_target);
                RETURN NULL;
            END $$;
            CREATE TRIGGER expire_after_conflict AFTER INSERT ON leases
                FOR EACH STATEMENT EXECUTE FUNCTION expire_lease_after_conflict();
            """))
            await setup.ExecuteNonQueryAsync(Token);
        await using (var target = database.DataSource.CreateCommand("INSERT INTO lease_expiry_target VALUES($1)"))
        {
            target.Parameters.AddWithValue(paused.RequireLease().Holder);
            await target.ExecuteNonQueryAsync(Token);
        }
        try
        {
            var elapsed = Stopwatch.StartNew();
            await standby.StartAsync(Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            while (!standby.IsLeader) await Task.Delay(1, timeout.Token);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), elapsed.Elapsed.ToString());
            await using var connection = await database.DataSource.OpenConnectionAsync(Token);
            await using var transaction = await connection.BeginTransactionAsync(Token);
            await standby.FenceAsync(connection, transaction, standby.RequireLease(), Token);
            await transaction.CommitAsync(Token);
        }
        finally
        {
            await standby.StopAsync(Token);
            await using var cleanup = database.DataSource.CreateCommand("""
                DROP TRIGGER expire_after_conflict ON leases;
                DROP FUNCTION expire_lease_after_conflict();
                DROP TABLE lease_expiry_target;
                """);
            await cleanup.ExecuteNonQueryAsync(Token);
        }
    }

    [Fact]
    public async Task LeasePausedFormerLeaderCannotWriteAfterNewEpochAndSameHolderReacquisitionAlsoFencesOldToken()
    {
        await Reset();
        using var first = Manager(5100);
        using var second = Manager(5101);
        Assert.True(await first.TryAcquireOrRenewAsync(Token));
        var old = first.RequireLease();
        await Expire();
        Assert.True(await second.TryAcquireOrRenewAsync(Token));
        Assert.True(first.IsLeader);
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
            await Assert.ThrowsAsync<NotLeaderException>(() => first.FenceAsync(connection, transaction, old, Token));
        Assert.False(first.IsLeader);
        await second.ReleaseAsync(Token);
        Assert.True(await first.TryAcquireOrRenewAsync(Token));
        Assert.True(first.RequireLease().Epoch > old.Epoch);
        await using var resumed = await database.DataSource.OpenConnectionAsync(Token);
        await using var resumedTransaction = await resumed.BeginTransactionAsync(Token);
        await Assert.ThrowsAsync<NotLeaderException>(() => first.FenceAsync(resumed, resumedTransaction, old, Token));
        Assert.True(first.IsLeader);
    }

    [Fact]
    public async Task LeaseFencedTransactionTimesOutAndRollsBackWhenClientPauses()
    {
        await Reset();
        using var first = Manager(5100);
        using var second = Manager(5101);
        Assert.True(await first.TryAcquireOrRenewAsync(Token));
        await using (var create = database.DataSource.CreateCommand(
            "CREATE TABLE IF NOT EXISTS lease_probe(id uuid PRIMARY KEY)"))
            await create.ExecuteNonQueryAsync(Token);
        var id = Guid.CreateVersion7();
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await first.FenceAsync(connection, transaction, first.RequireLease(), Token);
        await using (var insert = new NpgsqlCommand("INSERT INTO lease_probe VALUES($1)", connection, transaction))
        {
            insert.Parameters.AddWithValue(id);
            await insert.ExecuteNonQueryAsync(Token);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(1250), Token);
        await Expire();
        Assert.True(await second.TryAcquireOrRenewAsync(Token));
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => transaction.CommitAsync(Token));
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM lease_probe WHERE id=$1");
        count.Parameters.AddWithValue(id);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task LeaseFencedGroupCommitRetainsUnacknowledgedEventsAcrossActualTransactionTimeout()
    {
        await Reset();
        using var leader = Manager(5100);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
        var run = NewRun();
        await store.CreateAsync(run, "tests/transaction-timeout", Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddHours(1), Token);
        await using var appender = new GroupCommitAppender(database.DataSource,
            NullLogger<GroupCommitAppender>.Instance, leadership: leader);
        Task<GroupCommitReceipt> pending;
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await using (var block = new NpgsqlCommand("LOCK TABLE run_events IN ACCESS EXCLUSIVE MODE", connection))
                await block.ExecuteNonQueryAsync(Token);
            pending = appender.AppendAsync(run.CreateEvent(EventEntity.Run, null,
                (int)RunStatus.Running, DateTimeOffset.UtcNow), Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                await using var query = database.DataSource.CreateCommand("""
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                        AND wait_event_type='Lock' AND query LIKE '%INSERT INTO run_events%')
                    """);
                if (Equals(true, await query.ExecuteScalarAsync(timeout.Token))) break;
                await Task.Delay(10, timeout.Token);
            }
            // The production fence terminates a blocked transaction after at most one second.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), Token);
            Assert.False(pending.IsCompleted);
            await transaction.CommitAsync(Token);
        }
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(5), Token)).EventCount);
        Assert.Equal(1, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM run_events WHERE run_id=$1");
        count.Parameters.AddWithValue(run.Snapshot.RunId);
        Assert.Equal(1L, await count.ExecuteScalarAsync(Token));
        await leader.ReleaseAsync(Token);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("append")]
    [InlineData("dispatch")]
    [InlineData("results")]
    [InlineData("appender")]
    [InlineData("register")]
    [InlineData("heartbeat")]
    [InlineData("sweep")]
    [InlineData("pending")]
    public async Task LeaseEveryHostedEngineSurfaceRejectsOldEpochBeforeMutation(string surface)
    {
        await Reset();
        using var old = Manager(5100);
        using var current = Manager(5101);
        Assert.True(await old.TryAcquireOrRenewAsync(Token));
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, old);
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System, old);
        var hello = Hello();
        var session = await registry.RegisterAsync(hello, Token);
        var run = NewRun();
        await store.CreateAsync(run, "team/type/test", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        foreach (var (entity, key, state) in new[] {
            (EventEntity.Run, (string?)null, (int)RunStatus.Running),
            (EventEntity.Node, "node", (int)ExecutionStatus.Ready),
            (EventEntity.Node, "node", (int)ExecutionStatus.Dispatched),
            (EventEntity.Node, "node", (int)ExecutionStatus.Running),
            (EventEntity.Task, "node/task", (int)ExecutionStatus.Ready)
        })
            await store.AppendAsync(run, entity, key, state, DateTimeOffset.UtcNow, Token);
        var sequence = run.Snapshot.LastSequence;
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), TimeProvider.System);
        await using var appender = new GroupCommitAppender(database.DataSource,
            NullLogger<GroupCommitAppender>.Instance, leadership: old);
        await Expire();
        Assert.True(await current.TryAcquireOrRenewAsync(Token));
        Assert.True(old.IsLeader);
        async Task Write()
        {
            switch (surface)
            {
                case "create":
                    await store.CreateAsync(NewRun(), "team/type/test", Guid.CreateVersion7(),
                        DateTimeOffset.UtcNow.AddHours(1), Token);
                    break;
                case "append":
                    await store.AppendAsync(run, EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched,
                        DateTimeOffset.UtcNow, Token);
                    break;
                case "dispatch":
                    var sha = new string('a', 64);
                    await dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, "node", "task", 1, sha),
                        new Wf.Contracts.Agent.V1.Dispatch {
                            Task = Reference(run), Version = "1.0.0", Sha256 = sha,
                            PackageUri = "http://localhost/package.zip",
                            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1))
                        }, Token);
                    break;
                case "results":
                    await store.AcceptAsync(session, new AgentMessage {
                        Sequence = 2, TaskStarted = new TaskStarted {
                            Task = Reference(run), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                        }
                    }, Token);
                    break;
                case "appender":
                    await appender.AppendAsync(run.CreateEvent(EventEntity.Task, "node/task",
                        (int)ExecutionStatus.Dispatched, DateTimeOffset.UtcNow), Token);
                    break;
                case "register": await registry.RegisterAsync(Hello(), Token); break;
                case "heartbeat": await registry.HeartbeatAsync(session, Heartbeat(), Token); break;
                case "sweep": await registry.SweepAsync(Token); break;
                case "pending": await store.PendingAsync(session, Token); break;
                default: throw new InvalidOperationException("Unknown engine surface.");
            }
        }
        await Assert.ThrowsAsync<NotLeaderException>(Write);
        Assert.False(old.IsLeader);
        Assert.Equal(sequence, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(ExecutionStatus.Ready, run.Snapshot.Tasks["node/task"]);
        await using var dispatches = database.DataSource.CreateCommand(
            "SELECT count(*) FROM dispatch_outbox WHERE run_id=$1");
        dispatches.Parameters.AddWithValue(run.Snapshot.RunId);
        Assert.Equal(0L, await dispatches.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task LeaseRealGatewayRedirectsStandbyAndAcknowledgesWithActualNewEpoch()
    {
        await Reset();
        using var leader = Manager(5100);
        using var standby = Manager(5101);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        Assert.False(await standby.TryAcquireOrRenewAsync(Token));
        using var leaderRegistry = new AgentRegistry(database.DataSource, TimeProvider.System, leader);
        using var standbyRegistry = new AgentRegistry(database.DataSource, TimeProvider.System, standby);
        await using var first = await Gateway(leader, leaderRegistry);
        await using var second = await Gateway(standby, standbyRegistry);
        using var http = new HttpClient { DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using (var ready = await http.GetAsync(new Uri(Address(first), "/healthz/leader"), Token))
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using (var notReady = await http.GetAsync(new Uri(Address(second), "/healthz/leader"), Token))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        using var firstChannel = GrpcChannel.ForAddress(Address(first));
        using var secondChannel = GrpcChannel.ForAddress(Address(second));
        using var rejected = new AgentGateway.AgentGatewayClient(secondChannel).Connect(cancellationToken: Token);
        var hello = Hello();
        await rejected.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Hello = hello }, Token);
        Assert.True(await rejected.ResponseStream.MoveNext(Token));
        Assert.Equal(leader.LeaderHint, rejected.ResponseStream.Current.NotLeader.LeaderHint);
        Assert.Null(standbyRegistry.Find(Guid.Parse(hello.Id)));
        using var accepted = new AgentGateway.AgentGatewayClient(firstChannel).Connect(cancellationToken: Token);
        await accepted.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Hello = hello }, Token);
        Assert.True(await accepted.ResponseStream.MoveNext(Token));
        Assert.Equal(checked((ulong)leader.RequireLease().Epoch), accepted.ResponseStream.Current.LeaderEpoch);
        Assert.Equal(1UL, accepted.ResponseStream.Current.Ack.Sequence);
        await leader.ReleaseAsync(Token);
        Assert.True(await standby.TryAcquireOrRenewAsync(Token));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        Assert.True(await accepted.ResponseStream.MoveNext(timeout.Token));
        Assert.NotNull(accepted.ResponseStream.Current.NotLeader);
        using var reconnected = new AgentGateway.AgentGatewayClient(secondChannel).Connect(cancellationToken: Token);
        await reconnected.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Hello = hello }, Token);
        Assert.True(await reconnected.ResponseStream.MoveNext(Token));
        Assert.Equal(2UL, reconnected.ResponseStream.Current.LeaderEpoch);
        Assert.Equal(1UL, reconnected.ResponseStream.Current.Ack.Sequence);
        using var nowReady = await http.GetAsync(new Uri(Address(second), "/healthz/leader"), Token);
        Assert.Equal(HttpStatusCode.OK, nowReady.StatusCode);
    }

    [Fact]
    public async Task LeaderConnector_actual_agent_follows_standby_hint_then_reconnects_after_database_lease_takeover()
    {
        await Reset();
        await using var first = await Gateway();
        await using var second = await Gateway();
        var leader = first.Services.GetRequiredService<LeaseManager>();
        var standby = second.Services.GetRequiredService<LeaseManager>();
        var firstRegistry = first.Services.GetRequiredService<AgentRegistry>();
        var secondRegistry = second.Services.GetRequiredService<AgentRegistry>();
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        Assert.False(await standby.TryAcquireOrRenewAsync(Token));
        Assert.Equal(Address(first).AbsoluteUri, standby.LeaderHint);
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "wf-leader-connector-" + id.ToString("N"));
        try
        {
            using var journal = new ResultJournal(root, id.ToString());
            using var outbox = new AgentOutbox(journal);
            var options = new AgentOptions([Address(second), Address(first)], id.ToString(),
                "leader-connector-" + id.ToString("N"), "connector", 1,
                heartbeatInterval: TimeSpan.FromMilliseconds(100), resultJournalDirectory: root);
            var probe = new ConnectorProbe();
            using var connector = new AgentConnection(options, probe, probe, TimeProvider.System,
                NullLogger<AgentConnection>.Instance, outbox);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var running = connector.RunAsync(stopping.Token);
            try
            {
                await WaitForAcknowledgedSession(firstRegistry);
                Assert.Equal(2, probe.Hellos);
                Assert.Null(secondRegistry.Find(id));
                Assert.Equal(id, firstRegistry.Find(id)!.Id);
                await leader.ReleaseAsync(Token);
                Assert.True(await standby.TryAcquireOrRenewAsync(Token));
                Assert.False(await leader.TryAcquireOrRenewAsync(Token));
                Assert.Equal(Address(second).AbsoluteUri, leader.LeaderHint);
                await WaitForAcknowledgedSession(secondRegistry);
                Assert.Equal(id, secondRegistry.Find(id)!.Id);
                Assert.NotEqual(firstRegistry.Find(id)!.SessionId, secondRegistry.Find(id)!.SessionId);
                Assert.Equal(2L, standby.RequireLease().Epoch);
                Assert.Throws<InvalidDataException>(() => outbox.ObserveLeaderEpoch(1));
                Assert.False(running.IsCompleted);
            }
            finally
            {
                await stopping.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(5), Token);
            }

            async Task WaitForAcknowledgedSession(AgentRegistry registry)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                while (registry.Find(id) is null)
                {
                    if (running.IsCompleted) await running;
                    await Task.Delay(10, deadline.Token);
                }
                outbox.Enqueue(new AgentMessage { Heartbeat = Heartbeat() });
                while (outbox.NextAfter(0) is not null)
                {
                    if (running.IsCompleted) await running;
                    await Task.Delay(10, deadline.Token);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class ConnectorProbe : IAgentTelemetry, IAgentCommandHandler
    {
        private readonly AgentTelemetry telemetry = new(TimeProvider.System);
        private int hellos;
        public int Hellos => Volatile.Read(ref hellos);
        public Hello CreateHello(AgentOptions options)
        {
            Interlocked.Increment(ref hellos);
            return telemetry.CreateHello(options);
        }
        public Heartbeat Sample() => telemetry.Sample();
        public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No task commands are expected during leader-connector acceptance.");
    }

    private LeaseManager Manager(int port) => new(database.DataSource,
        new(Guid.NewGuid().ToString("N"), new Uri($"http://localhost:{port}")), NullLogger<LeaseManager>.Instance);

    private async Task Reset()
    {
        await using var command = database.DataSource.CreateCommand("DELETE FROM leases");
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task Expire()
    {
        await using var command = database.DataSource.CreateCommand(
            "UPDATE leases SET expires_at=clock_timestamp()-interval '1 second' WHERE name='engine'");
        await command.ExecuteNonQueryAsync(Token);
    }

    private static RunState NewRun() => new(Guid.CreateVersion7(), [new NodeDefinition("node", [], [new("task")])],
        new WorkflowMetadata("type", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);

    private static AttemptRef Reference(RunState run) => new() {
        RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "node", TaskKey = "task", Attempt = 1
    };

    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new() { Id = id.ToString(), Name = $"lease-agent-{id:N}", Os = "linux", Arch = "x64",
            Cores = 4, RamMb = 8192, MaxTasks = 2, Pool = "test", Version = "1.0", ProtocolVersion = 1 };
    }

    private static Heartbeat Heartbeat() => new() { At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };

    private async Task<WebApplication> Gateway(LeaseManager? leadership = null, AgentRegistry? registry = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        if (leadership is not null) builder.Services.AddSingleton(leadership);
        else builder.Services.AddSingleton(provider => new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new(provider.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single())), NullLogger<LeaseManager>.Instance));
        if (registry is not null) builder.Services.AddSingleton(registry);
        else builder.Services.AddSingleton(provider => new AgentRegistry(database.DataSource, TimeProvider.System,
            provider.GetRequiredService<LeaseManager>()));
        builder.Services.AddSingleton(provider => new RunStore(database.DataSource, NullLogger<RunStore>.Instance,
            provider.GetRequiredService<LeaseManager>()));
        var app = builder.Build();
        app.MapGrpcService<AgentGatewayService>();
        app.MapGet("/healthz/leader", (LeaseManager manager) => manager.IsLeader ? Results.Ok() : Results.StatusCode(503));
        await app.StartAsync(Token);
        return app;
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()!.Addresses.Single());
}

public sealed class LeaseDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql", "0009_leases.sql",
            "0011_attempt_replacement.sql"];
}
