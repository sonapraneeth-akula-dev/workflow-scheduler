using System.Collections.Immutable;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;
using Clock = Wf.Integration.Tests.Registration.RegistrationTests.Clock;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests;

public sealed partial class ReservationManagerTests(ReservationDatabase database, ITestOutputHelper output) : IClassFixture<ReservationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly Clock clock = new();
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance, timeProvider: clock);
    private ReservationManager Manager => new(database.DataSource, Store, clock);

    [Theory]
    [InlineData("ids")]
    [InlineData("labels")]
    [InlineData("names")]
    public async Task Exact_count_intersects_current_machine_metadata_and_retries_preserve_identity_epoch_and_expiry(string kind)
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 4);
        var run = await Ready();
        var selector = kind switch
        {
            "ids" => new PlacementSelector(os: "windows", machineIds: fleet.Select(s => s.AgentId).ToImmutableArray(), count: 2),
            "names" => new PlacementSelector(machines: fleet[..2].Select(s => new SelectorValue(literal: Name(s.AgentId))).ToImmutableArray()),
            _ => new PlacementSelector(os: "windows", pool: Pool(fleet), labels: [new("role", "perf"), new("capability")], count: 2)
        };
        var allocated = await Manager.ReserveAsync(run.Snapshot.RunId, "work", 2, selector, Lifetime, Token);
        Assert.Equal(fleet[..2].Select(s => s.AgentId).Order(), allocated.Select(r => r.AgentId));
        Assert.All(allocated, reservation =>
        {
            Assert.Equal(7, reservation.Id.Version);
            Assert.True(reservation.Epoch > 1);
            Assert.Equal(run.Snapshot.RunId, reservation.RunId);
            Assert.Equal("work", reservation.NodeKey);
            Assert.Equal(reservation.CreatedAt, reservation.RenewedAt);
            Assert.Equal(Lifetime, reservation.ExpiresAt - reservation.CreatedAt);
        });
        Assert.Equal(2, allocated.Select(r => r.Epoch).Distinct().Count());
        clock.Now += TimeSpan.FromSeconds(1);
        var equivalent = new PlacementSelector(selector.Os, selector.Pool, selector.Labels,
            selector.Machines.Reverse().ToImmutableArray(), selector.MachineIds.Reverse().ToImmutableArray(), selector.Count);
        Assert.Equal(allocated, await Manager.ReserveAsync(run.Snapshot.RunId, "work", 2, equivalent, Lifetime + Lifetime, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.ReserveAsync(run.Snapshot.RunId, "work", 1, new(pool: Pool(fleet)), Lifetime, Token));
        Assert.Equal(2L, await Count(run.Snapshot.RunId));
    }

    [Fact]
    public async Task Concurrent_engines_reserve_four_agents_for_only_two_exact_sized_nodes_without_partial_losers()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 4);
        var runs = new List<RunState>();
        for (var index = 0; index < 20; index++) runs.Add(await Ready());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = runs.Select(async run =>
        {
            await start.Task;
            try { return await Manager.ReserveAsync(run.Snapshot.RunId, "work", 2, new(pool: Pool(fleet)), Lifetime, Token); }
            catch (ReservationUnavailableException) { return []; }
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(2, results.Count(result => result.Count == 2));
        Assert.Equal(18, results.Count(result => result.Count == 0));
        Assert.Equal(4, results.SelectMany(result => result).Select(r => r.AgentId).Distinct().Count());
        foreach (var run in runs)
            Assert.Equal(results.SingleOrDefault(result => result.Count > 0 && result[0].RunId == run.Snapshot.RunId)?.Count ?? 0,
                await Count(run.Snapshot.RunId));
        var winner = results.First(result => result.Count > 0);
        var retries = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Manager.ReserveAsync(winner[0].RunId, "work", 2, new(pool: Pool(fleet)), Lifetime, Token)));
        Assert.All(retries, retry => Assert.Equal(winner, retry));
    }

    [Fact]
    public async Task Second_row_failure_rolls_back_first_row_and_leaves_all_machines_available()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var run = await Ready();
        var rejected = fleet.Max(s => s.AgentId);
        await Execute($"ALTER TABLE reservations ADD CONSTRAINT reject_reservation_test CHECK (run_id <> '{run.Snapshot.RunId:D}' OR agent_id <> '{rejected:D}')");
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                Manager.ReserveAsync(run.Snapshot.RunId, "work", 2, new(pool: Pool(fleet)), Lifetime, Token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal(0L, await Count(run.Snapshot.RunId));
        }
        finally { await Execute("ALTER TABLE reservations DROP CONSTRAINT reject_reservation_test"); }
        Assert.Equal(2, (await Manager.ReserveAsync(run.Snapshot.RunId, "work", 2, new(pool: Pool(fleet)), Lifetime, Token)).Count);
    }

    [Fact]
    public async Task Concurrent_dispatch_and_reservation_share_one_atomic_machine_allocation_boundary()
    {
        for (var iteration = 0; iteration < 12; iteration++)
        {
            using var registry = new AgentRegistry(database.DataSource, clock);
            var fleet = await Fleet(registry, 1);
            var owner = await Ready();
            var foreign = await Ready();
            var selector = new PlacementSelector(machineIds: [fleet[0].AgentId]);
            using var dispatcher = new Dispatcher(database.DataSource, Store, registry, new(), clock);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reservationTask = Reserve();
            var dispatchTask = Dispatch();
            start.SetResult();
            var reserved = await reservationTask;
            var dispatched = await dispatchTask;
            Assert.NotEqual(reserved, dispatched);
            Assert.Equal(reserved ? 1L : 0L, await Count(owner.Snapshot.RunId));
            Assert.Equal(dispatched ? 1L : 0L,
                await Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1", foreign.Snapshot.RunId));

            async Task<bool> Reserve()
            {
                await start.Task;
                try
                {
                    await Manager.ReserveAsync(owner.Snapshot.RunId, "work", 1, selector, Lifetime, Token);
                    return true;
                }
                catch (ReservationUnavailableException) { return false; }
            }
            async Task<bool> Dispatch()
            {
                await start.Task;
                return (await Send(dispatcher, foreign, "work", "first", selector)).Agent is not null;
            }
        }
    }

    [Theory]
    [InlineData("unhealthy")]
    [InlineData("draining")]
    [InlineData("stale")]
    [InlineData("slots")]
    [InlineData("dispatched")]
    [InlineData("running")]
    [InlineData("retrying")]
    [InlineData("pool")]
    [InlineData("label")]
    public async Task Fresh_database_not_cached_registry_prevents_reserving_unavailable_or_nonmatching_agents(string reason)
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var blocked = fleet[1].AgentId;
        if (reason is "dispatched" or "running" or "retrying")
        {
            var foreign = await Ready();
            var status = reason == "dispatched" ? 2 : reason == "running" ? 3 : 4;
            await Execute($"""
                INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,deadline_at)
                VALUES(uuid_v7_run_day('{foreign.Snapshot.RunId:D}'),'{foreign.Snapshot.RunId:D}','work/first',1,'{blocked:D}',{status},now()+interval '1 minute')
                """);
        }
        else
            await Execute($"UPDATE agents SET {reason switch
            {
                "unhealthy" => "status=1", "draining" => "status=2",
                "stale" => "last_heartbeat_at=now()-interval '1 minute'",
                "slots" => "slots_used=1", "pool" => "pool='changed'",
                "label" => "labels=ARRAY['role=wrong','capability=load']",
                _ => throw new ArgumentOutOfRangeException(nameof(reason))
            }} WHERE id='{blocked:D}'");
        Assert.True(registry.Find(blocked)!.IsDispatchable(clock.Now));
        var run = await Ready();
        var selector = new PlacementSelector(pool: Pool(fleet), labels: [new("role", "perf")]);
        await Assert.ThrowsAsync<ReservationUnavailableException>(() =>
            Manager.ReserveAsync(run.Snapshot.RunId, "work", 2, selector, Lifetime, Token));
        Assert.Equal(0L, await Count(run.Snapshot.RunId));
        Assert.Equal(fleet[0].AgentId, Assert.Single(
            await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1, selector, Lifetime, Token)).AgentId);
    }

    [Fact]
    public async Task Dispatcher_enforces_owner_node_boundary_without_spilling_and_persists_actual_epoch()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var owner = await Ready();
        var foreign = await Ready();
        var allocation = Assert.Single(await Manager.ReserveAsync(owner.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        using var dispatcher = new Dispatcher(database.DataSource, Store, registry, new(), clock);
        var owned = await Send(dispatcher, owner, "work", "first", new(pool: Pool(fleet)));
        Assert.Equal(allocation.AgentId, owned.Agent!.Id);
        Assert.Equal(allocation.Epoch, owned.Agent.ReservationEpoch);
        Assert.Null((await Send(dispatcher, owner, "work", "second", new(pool: Pool(fleet)))).Agent);
        var outsider = await Send(dispatcher, foreign, "work", "first", new(pool: Pool(fleet)));
        Assert.Equal(fleet[1].AgentId, outsider.Agent!.Id);
        Assert.Null((await Send(dispatcher, owner, "other", "first", new(machineIds: [fleet[0].AgentId]))).Agent);
        var command = Assert.Single(await Store.PendingAsync(fleet[0], Token)).Command;
        Assert.Equal((ulong)allocation.Epoch, command.ReservationEpoch);
        await using (var payload = database.DataSource.CreateCommand("SELECT dispatch_payload FROM task_attempts WHERE run_id=$1"))
        {
            payload.Parameters.AddWithValue(owner.Snapshot.RunId);
            var stored = DispatchCommand.Parser.ParseFrom((byte[])(await payload.ExecuteScalarAsync(Token))!);
            Assert.Equal(command.ReservationEpoch, stored.ReservationEpoch);
        }
        await Store.AcceptAsync(fleet[0], new AgentMessage { TaskStarted = new()
            { Task = command.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.Now),
                ReservationEpoch = command.ReservationEpoch } }, Token);
        await Store.AcceptAsync(fleet[0], new AgentMessage { TaskCompleted = new()
            { Task = command.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.Now), Outcome = TaskOutcome.Succeeded,
                ReservationEpoch = command.ReservationEpoch } }, Token);
        owner = await Store.LoadAsync(owner.Snapshot.RunId, Token);
        using var restoredRegistry = new AgentRegistry(database.DataSource, clock);
        await restoredRegistry.LoadAsync(Token);
        using var restarted = new Dispatcher(database.DataSource, Store, restoredRegistry, new(), clock);
        Assert.Equal(fleet[0].AgentId, (await Send(restarted, owner, "work", "second", new(pool: Pool(fleet)))).Agent!.Id);
        Assert.Equal(1L, await Count(owner.Snapshot.RunId));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("unhealthy")]
    [InlineData("absent")]
    public async Task An_undispatchable_owned_cohort_never_falls_through_to_unreserved_machines(string reason)
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var owner = await Ready();
        await Manager.ReserveAsync(owner.Snapshot.RunId, "work", 1, new(machineIds: [fleet[0].AgentId]), Lifetime, Token);
        if (reason == "expired")
        {
            clock.Now += Lifetime;
            foreach (var session in fleet)
                await registry.HeartbeatAsync(session, new Heartbeat { At = Timestamp.FromDateTimeOffset(clock.Now) }, Token);
            await Assert.ThrowsAsync<ReservationUnavailableException>(() =>
                Manager.ReserveAsync(owner.Snapshot.RunId, "work", 1, new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
            var foreign = await Ready();
            await Assert.ThrowsAsync<ReservationUnavailableException>(() =>
                Manager.ReserveAsync(foreign.Snapshot.RunId, "work", 1, new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        }
        else
        {
            await Execute($"UPDATE agents SET status=1 WHERE id='{fleet[0].AgentId:D}'");
        }
        using var freshRegistry = new AgentRegistry(database.DataSource, clock);
        if (reason != "absent") await freshRegistry.LoadAsync(Token);
        else
        {
            var hello = Hello(fleet[1].AgentId, Pool(fleet), "windows");
            await freshRegistry.RegisterAsync(hello, Token);
        }
        using var dispatcher = new Dispatcher(database.DataSource, Store, freshRegistry, new(), clock);
        Assert.Null((await Send(dispatcher, owner, "work", "first", new(pool: Pool(fleet)))).Agent);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1", owner.Snapshot.RunId));
        Assert.Equal(1L, await Count(owner.Snapshot.RunId));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("node")]
    [InlineData("count")]
    [InlineData("selector-count")]
    [InlineData("id-count")]
    [InlineData("variable")]
    [InlineData("lifetime")]
    public async Task Invalid_reservation_inputs_fail_without_database_writes(string reason)
    {
        var run = await Ready();
        var runId = reason == "id" ? Guid.NewGuid() : run.Snapshot.RunId;
        var selector = reason switch
        {
            "selector-count" => new PlacementSelector(count: 2),
            "id-count" => new PlacementSelector(machineIds: [Guid.NewGuid()]),
            "variable" => new PlacementSelector(pool: new(variable: "pool")),
            _ => new PlacementSelector()
        };
        var count = reason == "count" ? 0 : reason == "id-count" ? 2 : 1;
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Manager.ReserveAsync(runId, reason == "node" ? "work/task" : "work", count, selector,
                reason == "lifetime" ? TimeSpan.Zero : Lifetime, Token));
        Assert.Equal(0L, await Count(run.Snapshot.RunId));
    }

    [Fact]
    public async Task Unknown_nodes_terminal_runs_and_terminal_nodes_cannot_reserve()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var run = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.ReserveAsync(run.Snapshot.RunId, "missing", 1, new(pool: Pool(fleet)), Lifetime, Token));
        await Execute($"UPDATE node_runs SET status=7 WHERE run_id='{run.Snapshot.RunId:D}' AND node_key='work'");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.ReserveAsync(run.Snapshot.RunId, "work", 1, new(pool: Pool(fleet)), Lifetime, Token));
        await Execute($"UPDATE runs SET status=2 WHERE id='{run.Snapshot.RunId:D}'");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.ReserveAsync(run.Snapshot.RunId, "other", 1, new(pool: Pool(fleet)), Lifetime, Token));
        Assert.Equal(0L, await Count(run.Snapshot.RunId));
    }

    [Fact]
    public async Task Paused_former_leader_cannot_create_a_reservation_after_database_lease_takeover()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var run = await Ready();
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new("http://localhost:5544")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        try
        {
            await Execute("UPDATE leases SET epoch=epoch+1,holder='replacement' WHERE name='engine'");
            var fencedStore = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            await Assert.ThrowsAsync<NotLeaderException>(() =>
                new ReservationManager(database.DataSource, fencedStore, clock)
                    .ReserveAsync(run.Snapshot.RunId, "work", 1, new(pool: Pool(fleet)), Lifetime, Token));
            var fencedManager = new ReservationManager(database.DataSource, fencedStore, clock);
            await Assert.ThrowsAsync<NotLeaderException>(() => fencedManager.RequestAsync(run.Snapshot.RunId,
                "work", 1, new(pool: Pool(fleet)), Lifetime, cancellationToken: Token));
            await Assert.ThrowsAsync<NotLeaderException>(() => fencedManager.SweepQueueAsync(Token));
            Assert.Equal(0L, await Count(run.Snapshot.RunId));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", run.Snapshot.RunId));
        }
        finally { await Execute("DELETE FROM leases WHERE name='engine'"); }
    }

    [Fact]
    public async Task Embedded_migration_is_expand_only_and_database_enforces_active_agent_exclusivity_and_node_identity()
    {
        Assert.Empty(MigrationLint.Validate(Migrator.LoadScripts().Single(s => s.Name == "0028_reservations.sql").Contents));
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var run = await Ready();
        var allocation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => Execute($"""
            INSERT INTO reservations(id,run_day,run_id,node_key,agent_id,selector,created_at,renewed_at,expires_at)
            SELECT '{Guid.CreateVersion7():D}',run_day,run_id,'other',agent_id,selector,created_at,renewed_at,expires_at
            FROM reservations WHERE id='{allocation.Id:D}'
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var unknownNode = await Assert.ThrowsAsync<PostgresException>(() => Execute($"""
            INSERT INTO reservations(id,run_day,run_id,node_key,agent_id,selector,created_at,renewed_at,expires_at)
            SELECT '{Guid.CreateVersion7():D}',run_day,run_id,'missing','{fleet[1].AgentId:D}',selector,created_at,renewed_at,expires_at
            FROM reservations WHERE id='{allocation.Id:D}'
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownNode.SqlState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_production_migrator_supports_fresh_and_previous_release_schemas_without_rewriting_agents(bool upgrade)
    {
        var schema = $"reservation_migration_{Guid.NewGuid():N}";
        await Execute($"CREATE SCHEMA \"{schema}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { SearchPath = schema }.ConnectionString;
        try
        {
            var scripts = Migrator.LoadScripts();
            if (upgrade)
                Migrator.Upgrade(connectionString, NullLogger.Instance, scripts.Where(s => string.CompareOrdinal(s.Name, "0028") < 0));
            else
                Assert.Equal(scripts.Count, Migrator.Upgrade(connectionString, NullLogger.Instance).Count);
            await using var source = NpgsqlDataSource.Create(connectionString);
            var agent = await CapacityMigrationsTests.SeedAgent(source);
            await using var original = source.CreateCommand("SELECT xmin::text FROM agents WHERE id=$1");
            original.Parameters.AddWithValue(agent);
            var xmin = await original.ExecuteScalarAsync(Token);
            var installed = Migrator.Upgrade(connectionString, NullLogger.Instance);
            if (upgrade) Assert.Equal(scripts.Where(s => string.CompareOrdinal(s.Name, "0028") >= 0).Select(s => s.Name), installed);
            else Assert.Empty(installed);
            Assert.Equal(xmin, await original.ExecuteScalarAsync(Token));
            await using var journal = source.CreateCommand("SELECT count(*) FROM schema_versions");
            Assert.Equal((long)scripts.Count, await journal.ExecuteScalarAsync(Token));
            await using var count = source.CreateCommand("SELECT count(*) FROM reservations");
            Assert.Equal(0L, await count.ExecuteScalarAsync(Token));
        }
        finally { await Execute($"DROP SCHEMA \"{schema}\" CASCADE"); }
    }

    private async Task<RunState> Ready(string firstTask = "first")
    {
        var nodes = ImmutableArray.Create(
            new NodeDefinition("work", [], [new(firstTask), new("second")]),
            new NodeDefinition("other", [], [new("first")]));
        var run = new RunState(Guid.CreateVersion7(clock.Now), nodes,
            new("perf", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await Store.CreateAsync(run, "tests/perf/reservations", Guid.NewGuid(), clock.Now.AddHours(1), Token);
        await Store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, clock.Now, Token);
        foreach (var task in run.Snapshot.Tasks.Keys)
            await Store.AppendAsync(run, EventEntity.Task, task, (int)ExecutionStatus.Ready, clock.Now, Token);
        return run;
    }

    private static string Name(Guid id) => "reservation-" + id.ToString("N");
    private static string Pool(AgentSession[] fleet) => fleet[0].AgentId.ToString("N");
    private async Task<AgentSession[]> Fleet(AgentRegistry registry, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).Order().ToArray();
        var fleet = new List<AgentSession>();
        for (var index = 0; index < ids.Length; index++)
            fleet.Add(await registry.RegisterAsync(Hello(ids[index], ids[0].ToString("N"), index < 2 ? "windows" : "linux"), Token));
        return fleet.ToArray();
    }

    private static Hello Hello(Guid id, string pool, string os)
    {
        var hello = new Hello { Id = id.ToString("D"), Name = Name(id), Pool = pool, Os = os,
            Arch = "x64", Cores = 4, RamMb = 8192, MaxTasks = 1, Version = "1.0.0", ProtocolVersion = 1 };
        hello.Labels.Add("role", "perf");
        hello.Labels.Add("capability", "load");
        return hello;
    }

    private Task<PlacementDecision> Send(Dispatcher dispatcher, RunState run, string node, string task, PlacementSelector selector) =>
        dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, node, task, 1, Sha, new(selector)), new DispatchCommand
        {
            Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = node, TaskKey = task, Attempt = 1 },
            Sha256 = Sha, Version = "1.0.0", PackageUri = "https://packages.invalid/reservations.zip",
            DeadlineAt = Timestamp.FromDateTimeOffset(clock.Now.AddMinutes(1))
        }, Token);

    private Task<long> Count(Guid run) => Scalar("SELECT count(*) FROM reservations WHERE run_id=$1", run);
    private async Task<long> Scalar(string sql, Guid run)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(run);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
    private async Task Execute(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Token);
    }
}

public sealed class ReservationDatabase : AgentDatabase
{
    public string ConnectionString => DatabaseConnectionString;
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql",
            "0009_leases.sql", "0011_attempt_replacement.sql", "0025_resource_samples.sql"];
}
