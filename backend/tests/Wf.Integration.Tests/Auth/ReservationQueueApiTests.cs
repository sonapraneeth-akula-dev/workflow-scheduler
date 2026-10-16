using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    private static CancellationToken QueueToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reservation_queue_API_keeps_closing_reservation_visible_until_exact_physical_acknowledgement()
    {
        await using var scenario = await QueueScenario();
        await using var server = await Server.Start(database.DataSource, configure: app => app.MapReservationQueueEndpoints());
        using var leases = new ReservationLeaseService(database.DataSource, scenario.Store, TimeProvider.System,
            NullLogger<ReservationLeaseService>.Instance);
        await leases.ReleaseAsync(scenario.Owner.Snapshot.RunId, "work", "Awaiting physical containment", QueueToken);
        using var response = await server.GetPath(QueuePath(scenario.Second), server.Token(group: scenario.Group));
        var row = Assert.Single((await QueueResponse(response)).Items);
        Assert.Equal(2, row.Position);
        Assert.Equal(scenario.Blocker.Id, row.BlockingReservationId);
        Assert.Equal(scenario.Owner.Snapshot.RunId, row.BlockingRunId);
    }

    [Fact]
    public async Task Reservation_queue_API_reports_inconsistent_selector_data_without_a_success_fallback_or_echo()
    {
        await using var scenario = await QueueScenario();
        await using var query = database.DataSource.CreateCommand("""
            UPDATE reservation_queue SET selector='{"Pool":{"Variable":"must-not-be-echoed"}}'::jsonb WHERE run_id=$1
            """);
        query.Parameters.AddWithValue(scenario.Second.Snapshot.RunId);
        await query.ExecuteNonQueryAsync(QueueToken);
        await using var server = await Server.Start(database.DataSource, configure: app => app.MapReservationQueueEndpoints());
        using var response = await server.GetPath(QueuePath(scenario.Second), server.Token(group: scenario.Group));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("must-not-be-echoed", await response.Content.ReadAsStringAsync(QueueToken));
    }

    [Theory]
    [InlineData(RegistryRole.Viewer)]
    [InlineData(RegistryRole.Operator)]
    [InlineData(RegistryRole.Author)]
    [InlineData(RegistryRole.Admin)]
    public async Task Reservation_queue_API_returns_position_two_and_actual_blocking_reservation_for_every_viewer_role(RegistryRole role)
    {
        await using var scenario = await QueueScenario(role);
        await using var server = await Server.Start(database.DataSource, configure: app => app.MapReservationQueueEndpoints());
        using var response = await server.GetPath(QueuePath(scenario.Second), server.Token(group: scenario.Group));
        var queue = await QueueResponse(response);
        var item = Assert.Single(queue.Items);
        Assert.Equal(scenario.Second.Snapshot.RunId, queue.RunId);
        Assert.Equal("work", item.NodeKey);
        Assert.Equal(2, item.Position);
        Assert.Equal(1, item.Count);
        Assert.Equal(scenario.Blocker.Id, item.BlockingReservationId);
        Assert.Equal(scenario.Owner.Snapshot.RunId, item.BlockingRunId);
        var text = await response.Content.ReadAsStringAsync(QueueToken);
        foreach (var forbidden in new[] { "variables", "selector", "labels", "payload", "package", "epoch", scenario.Pool })
            Assert.DoesNotContain(forbidden, text);
    }

    [Fact]
    public async Task Reservation_queue_API_redacts_foreign_blocker_identity_and_rechecks_its_grant_without_renewing_JWT()
    {
        await using var scenario = await QueueScenario(foreignBlocker: true);
        await using var server = await Server.Start(database.DataSource, configure: app => app.MapReservationQueueEndpoints());
        var jwt = server.Token(group: scenario.Group);
        using (var before = await server.GetPath(QueuePath(scenario.Second), jwt))
        {
            var item = Assert.Single((await QueueResponse(before)).Items);
            Assert.Equal(2, item.Position);
            Assert.Null(item.BlockingReservationId);
            Assert.Null(item.BlockingRunId);
            var text = await before.Content.ReadAsStringAsync(QueueToken);
            Assert.DoesNotContain(scenario.Owner.Snapshot.RunId.ToString(), text);
            Assert.DoesNotContain(scenario.Blocker.Id.ToString(), text);
        }
        await Bind(scenario.Seed.OtherType, RegistryRole.Viewer, scenario.Group, RolePrincipalKind.Group, RoleScopeKind.Type);
        using (var granted = await server.GetPath(QueuePath(scenario.Second), jwt))
            Assert.Equal(scenario.Owner.Snapshot.RunId, Assert.Single((await QueueResponse(granted)).Items).BlockingRunId);
        await Save(new(scenario.Seed.Other, [new(scenario.Seed.Other, "release", "description", new([]))]));
        using (var revokedBlocker = await server.GetPath(QueuePath(scenario.Second), jwt))
            Assert.Null(Assert.Single((await QueueResponse(revokedBlocker)).Items).BlockingRunId);
        await Save(new(scenario.Seed.Team, [new(scenario.Seed.Team, "release", "description", new([]))]));
        using var revokedRun = await server.GetPath(QueuePath(scenario.Second), jwt);
        Assert.Equal(HttpStatusCode.Forbidden, revokedRun.StatusCode);
        Assert.True(revokedRun.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Reservation_queue_API_updates_positions_before_sweeping_ended_owners_and_hides_fulfilled_or_aborted_waits()
    {
        await using var scenario = await QueueScenario();
        await using var server = await Server.Start(database.DataSource, configure: app => app.MapReservationQueueEndpoints());
        var jwt = server.Token(group: scenario.Group);
        await scenario.Store.AppendAsync(scenario.First, EventEntity.Run, null, (int)RunStatus.Failed, DateTimeOffset.UtcNow, QueueToken);
        using (var changed = await server.GetPath(QueuePath(scenario.Second), jwt))
            Assert.Equal(1, Assert.Single((await QueueResponse(changed)).Items).Position);
        using (var ended = await server.GetPath(QueuePath(scenario.First), jwt))
            Assert.Empty((await QueueResponse(ended)).Items);
        await scenario.ReleaseOwner();
        await scenario.Manager.SweepQueueAsync(QueueToken);
        using (var granted = await server.GetPath(QueuePath(scenario.Second), jwt))
            Assert.Empty((await QueueResponse(granted)).Items);
        using (var noWait = await server.GetPath(QueuePath(scenario.Owner), jwt))
            Assert.Empty((await QueueResponse(noWait)).Items);
        var requeued = await scenario.CreateRun(scenario.Seed.Type);
        await scenario.Manager.RequestAsync(requeued.Snapshot.RunId, "work", 1, new(pool: scenario.Pool),
            TimeSpan.FromMinutes(2), cancellationToken: QueueToken);
        var now = DateTimeOffset.UtcNow;
        await scenario.Store.AppendAsync(requeued, requeued.CreateEvent(EventEntity.Run, null, (int)RunStatus.Aborting, now,
            new(Guid.NewGuid().ToString(), "Queue test", now, "Stop waiting", AbortSource.API)), QueueToken);
        using var aborted = await server.GetPath(QueuePath(requeued), jwt);
        Assert.Empty((await QueueResponse(aborted)).Items);
    }

    [Fact]
    public async Task Reservation_queue_API_denies_unknown_ungranted_or_unsigned_requests_and_invalid_identity_queries()
    {
        await using var scenario = await QueueScenario();
        await using var server = await Server.Start(database.DataSource, configure: app => app.MapReservationQueueEndpoints());
        var jwt = server.Token(group: scenario.Group);
        foreach (var path in new[]
        {
            "/api/v1/runs/invalid/reservation-queue",
            QueuePath(scenario.Second).Replace(scenario.Second.Snapshot.RunId.ToString(), scenario.Second.Snapshot.RunId.ToString().ToUpperInvariant()),
            QueuePath(scenario.Second) + "?limit=1",
        })
        {
            using var invalid = await server.GetPath(path, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var unknown = await server.GetPath($"/api/v1/runs/{Guid.CreateVersion7()}/reservation-queue", jwt))
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        using (var denied = await server.GetPath(QueuePath(scenario.Second), server.Token(group: Guid.NewGuid())))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var unsigned = await server.GetPath(QueuePath(scenario.Second), server.Token(group: scenario.Group, unsigned: true));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    private static string QueuePath(RunState run) => $"/api/v1/runs/{run.Snapshot.RunId:D}/reservation-queue";

    private static async Task<ReservationQueueResponse> QueueResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        return (await response.Content.ReadFromJsonAsync(ReservationQueueJsonContext.Default.ReservationQueueResponse, QueueToken))!;
    }

    private async Task<QueuedScenario> QueueScenario(RegistryRole role = RegistryRole.Viewer, bool foreignBlocker = false,
        Seeded? seed = null, Guid? group = null)
    {
        var principal = group ?? Guid.NewGuid();
        seed ??= await Seed(role, principal, false);
        var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var id = Guid.NewGuid();
        var pool = id.ToString("N");
        var session = await registry.RegisterAsync(new Hello
        {
            Id = id.ToString(), Name = "queue-api-" + pool, Pool = pool, Os = "linux", Arch = "x64",
            Cores = 4, RamMb = 8192, MaxTasks = 1, Version = "1.0.0", ProtocolVersion = 1
        }, QueueToken);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var manager = new ReservationManager(database.DataSource, store, TimeProvider.System);
        var scenario = new QueuedScenario(seed, principal, pool, database.DataSource, registry, session, store, manager,
            async type =>
            {
                var workflow = type + "/queue-" + Guid.NewGuid().ToString("N");
                await CatalogWorkflow(workflow);
                await using var query = database.DataSource.CreateCommand("SELECT active_version_id FROM workflows WHERE id=$1");
                query.Parameters.AddWithValue(workflow);
                var version = (Guid)(await query.ExecuteScalarAsync(QueueToken))!;
                var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
                    new("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
                await store.CreateAsync(run, workflow, version, DateTimeOffset.UtcNow.AddHours(1), QueueToken);
                await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, QueueToken);
                return run;
            });
        scenario.Owner = await scenario.CreateRun(foreignBlocker ? seed.OtherType : seed.Type);
        scenario.First = await scenario.CreateRun(seed.Type);
        scenario.Second = await scenario.CreateRun(seed.Type);
        scenario.Blocker = Assert.Single(await manager.ReserveAsync(scenario.Owner.Snapshot.RunId, "work", 1,
            new(pool: pool), TimeSpan.FromMinutes(2), QueueToken));
        await manager.RequestAsync(scenario.First.Snapshot.RunId, "work", 1, new(pool: pool),
            TimeSpan.FromMinutes(2), cancellationToken: QueueToken);
        await manager.RequestAsync(scenario.Second.Snapshot.RunId, "work", 1, new(pool: pool),
            TimeSpan.FromMinutes(2), cancellationToken: QueueToken);
        return scenario;
    }

    private sealed class QueuedScenario(Seeded seed, Guid group, string pool, NpgsqlDataSource source,
        AgentRegistry registry, AgentSession session, RunStore store, ReservationManager manager,
        Func<string, Task<RunState>> create) : IAsyncDisposable
    {
        private readonly List<Guid> runs = [];
        public Seeded Seed => seed;
        public Guid Group => group;
        public string Pool => pool;
        public RunStore Store => store;
        public ReservationManager Manager => manager;
        public RunState Owner { get; set; } = null!;
        public RunState First { get; set; } = null!;
        public RunState Second { get; set; } = null!;
        public MachineReservation Blocker { get; set; } = null!;

        public async Task<RunState> CreateRun(string type)
        {
            var run = await create(type);
            runs.Add(run.Snapshot.RunId);
            return run;
        }

        public async Task ReleaseOwner()
        {
            Assert.True(await registry.HeartbeatAsync(session,
                new() { At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) }, QueueToken));
            using var leases = new ReservationLeaseService(source, store, TimeProvider.System,
                NullLogger<ReservationLeaseService>.Instance);
            await leases.ReleaseAsync(Owner.Snapshot.RunId, "work", "Queue API integration release", QueueToken);
            foreach (var fence in await leases.PendingAsync(session, QueueToken))
                await leases.ConfirmAsync(session, fence.ReservationEpoch, QueueToken);
        }

        public async ValueTask DisposeAsync()
        {
            using var leases = new ReservationLeaseService(source, store, TimeProvider.System,
                NullLogger<ReservationLeaseService>.Instance);
            foreach (var id in runs)
            {
                var run = await store.LoadAsync(id, QueueToken);
                if (run.Snapshot.Status == RunStatus.Running)
                    await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Failed, DateTimeOffset.UtcNow, QueueToken);
                else if (run.Snapshot.Status == RunStatus.Aborting)
                    await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Aborted, DateTimeOffset.UtcNow, QueueToken);
                await leases.ReleaseAsync(id, "work", "Queue API integration cleanup", QueueToken);
            }
            foreach (var fence in await leases.PendingAsync(session, QueueToken))
                await leases.ConfirmAsync(session, fence.ReservationEpoch, QueueToken);
            await manager.SweepQueueAsync(QueueToken);
            registry.Dispose();
        }
    }
}
