using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core;
using static Wf.Chaos.Tests.ScheduleExactlyOnceTests;

namespace Wf.Chaos.Tests;

public sealed class ReservationFailoverTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(Timeout = 100_000)]
    public async Task Actual_hosted_queue_survives_leader_kill_and_grants_original_FIFO_without_polling_requests()
    {
        await using var database = await ChaosDatabase.StartAsync(Token, requireCommitTimestamps: false);
        var workflow = await SeedAsync(database.Source, Token, schedules: 0);
        await using var versionQuery = database.Source.CreateCommand("SELECT id FROM workflow_versions");
        var version = (Guid)(await versionQuery.ExecuteScalarAsync(Token))!;
        var root = Path.Combine(Path.GetTempPath(), "wf-queue-chaos-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var engines = new List<EngineProcess>();
        var completed = false;
        try
        {
            engines.Add(EngineProcess.Start(database.ConnectionString, root, 0));
            engines.Add(EngineProcess.Start(database.ConnectionString, root, 1));
            var firstLeader = await Leader(engines);
            var store = new RunStore(database.Source, NullLogger<RunStore>.Instance);
            var manager = new ReservationManager(database.Source, store, TimeProvider.System);
            var pool = Guid.NewGuid().ToString("N");
            var requests = new List<Guid>();
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
                    new("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
                await store.CreateAsync(run, workflow, version, DateTimeOffset.UtcNow.AddHours(1), Token);
                await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
                requests.Add(run.Snapshot.RunId);
                Assert.Equal(requests.Count, (await manager.RequestAsync(run.Snapshot.RunId, "work", 1,
                    new(pool: pool), TimeSpan.FromMinutes(2), cancellationToken: Token)).Position);
            }
            var before = await Queue();
            Assert.Equal(requests, before.Select(row => row.Run));
            output.WriteLine($"Hard-killing actual engine leader {firstLeader.Id} with two durable FIFO waiters.");
            await firstLeader.KillAsync();
            Assert.NotEqual(firstLeader.Id, (await Leader(engines)).Id);
            Assert.Equal(before, await Queue());
            using var registry = new AgentRegistry(database.Source, TimeProvider.System);
            var id = Guid.NewGuid();
            var session = await registry.RegisterAsync(new Hello
            {
                Id = id.ToString(), Name = "queue-chaos-" + id.ToString("N"), Pool = pool, Os = "linux",
                Arch = "x64", Cores = 4, RamMb = 8192, MaxTasks = 1, Version = "1.0.0", ProtocolVersion = 1
            }, Token);
            await Wait(async () => (await Read(database.Source)).Count == 1, engines);
            Assert.Equal(requests[0], Assert.Single(await Read(database.Source)).RunId);
            Assert.Equal(requests[1], Assert.Single(await Queue()).Run);
            using var leases = new ReservationLeaseService(database.Source, store, TimeProvider.System,
                NullLogger<ReservationLeaseService>.Instance);
            await leases.ReleaseAsync(requests[0], "work", "Grant next FIFO waiter", Token);
            await leases.ConfirmAsync(session, Assert.Single(await leases.PendingAsync(session, Token)).ReservationEpoch, Token);
            await Wait(async () => (await Read(database.Source)).Any(row => row.RunId == requests[1]), engines);
            Assert.Empty(await Queue());
            await leases.ReleaseAsync(requests[1], "work", "Hosted FIFO proof complete", Token);
            await leases.ConfirmAsync(session, Assert.Single(await leases.PendingAsync(session, Token)).ReservationEpoch, Token);
            Assert.Empty(await Read(database.Source));
            output.WriteLine("Replacement production engine granted original FIFO through physical-ACK database boundary; zero leaked reservations or queue rows.");
            completed = true;
        }
        finally
        {
            foreach (var engine in engines) await engine.DisposeAsync();
            if (!completed)
                foreach (var engine in engines)
                    foreach (var path in new[] { engine.LogPath, engine.LogPath + ".stderr" })
                        if (File.Exists(path)) output.WriteLine(await File.ReadAllTextAsync(path, CancellationToken.None));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        async Task<List<(long Position, Guid Run)>> Queue()
        {
            await using var command = database.Source.CreateCommand("SELECT position,run_id FROM reservation_queue ORDER BY position");
            await using var reader = await command.ExecuteReaderAsync(Token);
            var rows = new List<(long, Guid)>();
            while (await reader.ReadAsync(Token)) rows.Add((reader.GetInt64(0), reader.GetGuid(1)));
            return rows;
        }
    }

    [Theory(Timeout = 150_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_engine_leader_kills_adopt_same_reservations_before_expiry_and_replay_pending_fences_without_leaks(bool afterRenewal)
    {
        await using var database = await ChaosDatabase.StartAsync(Token, requireCommitTimestamps: false);
        var workflow = await SeedAsync(database.Source, Token, schedules: 0);
        await using var versionQuery = database.Source.CreateCommand("SELECT id FROM workflow_versions");
        var version = (Guid)(await versionQuery.ExecuteScalarAsync(Token))!;
        var root = Path.Combine(Path.GetTempPath(), "wf-rsv-chaos-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var engines = new List<EngineProcess>();
        var completed = false;
        try
        {
            engines.Add(EngineProcess.Start(database.ConnectionString, root, 0));
            engines.Add(EngineProcess.Start(database.ConnectionString, root, 1));
            var first = await Leader(engines);
            var epoch = await LeaderEpoch(database.Source);
            var store = new RunStore(database.Source, NullLogger<RunStore>.Instance);
            using var registry = new AgentRegistry(database.Source, TimeProvider.System);
            var sessions = new List<AgentSession>();
            var pool = Guid.NewGuid().ToString("N");
            foreach (var _ in Enumerable.Range(0, 4))
            {
                var id = Guid.NewGuid();
                sessions.Add(await registry.RegisterAsync(new Hello
                {
                    Id = id.ToString(), Name = "chaos-" + id.ToString("N"), Pool = pool, Os = "linux",
                    Arch = "x64", Cores = 4, RamMb = 8192, MaxTasks = 1, Version = "1.0.0", ProtocolVersion = 1
                }, Token));
            }
            var reservations = new List<MachineReservation>();
            var manager = new ReservationManager(database.Source, store, TimeProvider.System);
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
                    new("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
                await store.CreateAsync(run, workflow, version, DateTimeOffset.UtcNow.AddHours(1), Token);
                await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
                reservations.AddRange(await manager.ReserveAsync(run.Snapshot.RunId, "work", 2, new(pool: pool),
                    TimeSpan.FromSeconds(35), Token));
            }
            Assert.Equal(4, reservations.Select(row => row.AgentId).Distinct().Count());
            var original = reservations.ToDictionary(row => row.Id);
            if (afterRenewal)
                await Wait(async () => (await Read(database.Source)).All(row => row.RenewedAt > original[row.Id].RenewedAt), engines);
            var beforeKill = (await Read(database.Source)).ToDictionary(row => row.Id);
            output.WriteLine($"Killing actual leader PID {first.Id}, leader epoch {epoch}, afterRenewal={afterRenewal}; four active machine reservations.");
            var outage = Stopwatch.StartNew();
            await first.KillAsync();
            var second = await Leader(engines);
            Assert.NotEqual(first.Id, second.Id);
            Assert.True(await LeaderEpoch(database.Source) > epoch);
            await Wait(async () => (await Read(database.Source)).All(row => row.RenewedAt > beforeKill[row.Id].RenewedAt), engines);
            var adopted = await Read(database.Source);
            Assert.Equal(4, adopted.Count);
            foreach (var row in adopted)
            {
                Assert.Equal(original[row.Id].Epoch, row.Epoch);
                Assert.Equal(original[row.Id].AgentId, row.AgentId);
                Assert.True(row.RenewedAt < beforeKill[row.Id].ExpiresAt, "A new leader must renew before the old lease expires.");
                Assert.True(row.ExpiresAt > beforeKill[row.Id].ExpiresAt);
            }
            output.WriteLine($"Four identical reservation IDs/epochs renewed after hard kill in {outage.Elapsed.TotalSeconds:F2}s, before expiry.");
            using var service = new ReservationLeaseService(database.Source, store, TimeProvider.System,
                NullLogger<ReservationLeaseService>.Instance);
            await service.ReleaseAsync(reservations[0].RunId, "work", "Crash during pending physical fence", Token);
            var closingSessions = sessions.Where(session => reservations.Any(row =>
                row.RunId == reservations[0].RunId && row.AgentId == session.AgentId)).ToArray();
            var pending = new Dictionary<Guid, ulong>();
            foreach (var session in closingSessions)
                pending.Add(session.AgentId, Assert.Single(await service.PendingAsync(session, Token)).ReservationEpoch);
            engines.Add(EngineProcess.Start(database.ConnectionString, root, 2));
            var secondEpoch = await LeaderEpoch(database.Source);
            await second.KillAsync();
            var third = await Leader(engines);
            Assert.NotEqual(second.Id, third.Id);
            Assert.True(await LeaderEpoch(database.Source) > secondEpoch);
            using var rebuilt = new ReservationLeaseService(database.Source, new(database.Source, NullLogger<RunStore>.Instance),
                TimeProvider.System, NullLogger<ReservationLeaseService>.Instance);
            foreach (var session in closingSessions)
            {
                Assert.Equal(pending[session.AgentId], Assert.Single(await rebuilt.PendingAsync(session, Token)).ReservationEpoch);
                await rebuilt.ConfirmAsync(session, pending[session.AgentId], Token);
            }
            await rebuilt.ReleaseAsync(reservations[^1].RunId, "work", "Chaos complete", Token);
            foreach (var session in sessions.Except(closingSessions))
                await rebuilt.ConfirmAsync(session, Assert.Single(await rebuilt.PendingAsync(session, Token)).ReservationEpoch, Token);
            await using var remaining = database.Source.CreateCommand("SELECT count(*) FROM reservations WHERE released_at IS NULL");
            Assert.Equal(0L, await remaining.ExecuteScalarAsync(Token));
            await using var released = database.Source.CreateCommand("SELECT count(*) FROM reservations WHERE released_at IS NOT NULL");
            Assert.Equal(4L, await released.ExecuteScalarAsync(Token));
            output.WriteLine("Second leader kill preserved exact pending fences; all four reservations released, zero leaks.");
            completed = true;
        }
        finally
        {
            foreach (var engine in engines) await engine.DisposeAsync();
            if (!completed)
                foreach (var engine in engines)
                    foreach (var path in new[] { engine.LogPath, engine.LogPath + ".stderr" })
                        if (File.Exists(path)) output.WriteLine(await File.ReadAllTextAsync(path, CancellationToken.None));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<List<MachineReservation>> Read(NpgsqlDataSource source)
    {
        await using var query = source.CreateCommand("""
            SELECT id,run_id,node_key,agent_id,epoch,created_at,renewed_at,expires_at
            FROM reservations WHERE released_at IS NULL ORDER BY id
            """);
        await using var reader = await query.ExecuteReaderAsync(Token);
        var rows = new List<MachineReservation>();
        while (await reader.ReadAsync(Token))
            rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt64(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }

    private static async Task<long> LeaderEpoch(NpgsqlDataSource source)
    {
        await using var query = source.CreateCommand("SELECT epoch FROM leases WHERE name='engine'");
        return (long)(await query.ExecuteScalarAsync(Token))!;
    }

    private static async Task<EngineProcess> Leader(List<EngineProcess> engines)
    {
        EngineProcess? leader = null;
        await Wait(async () =>
        {
            foreach (var engine in engines.Where(engine => !engine.Killed))
                if (await engine.IsLeaderAsync(Token)) { leader = engine; return true; }
            return false;
        }, engines);
        return leader!;
    }

    private static async Task Wait(Func<Task<bool>> condition, List<EngineProcess> engines)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        while (!await condition())
        {
            foreach (var engine in engines.Where(engine => !engine.Killed))
                Assert.False(engine.HasExited, $"Engine {engine.Id} exited; inspect {engine.LogPath}.");
            await Task.Delay(100, timeout.Token);
        }
    }
}
