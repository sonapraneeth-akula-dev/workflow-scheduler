using Microsoft.Extensions.Logging.Abstractions;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core;

namespace Wf.Integration.Tests;

public sealed partial class ReservationManagerTests
{
    [Fact]
    public async Task Twenty_owners_churn_four_machines_with_concurrent_renewal_release_and_rebuilt_fence_delivery_without_leaks()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 4);
        var pending = new List<RunState>();
        foreach (var _ in Enumerable.Range(0, 20)) pending.Add(await Ready());
        var all = pending.Select(run => run.Snapshot.RunId).ToHashSet();
        var identities = new HashSet<Guid>();
        for (var round = 0; round < 5; round++)
        {
            var allocations = await Task.WhenAll(pending.Select(async run =>
            {
                try
                {
                    return await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1, new(pool: Pool(fleet)), Lifetime, Token);
                }
                catch (ReservationUnavailableException) { return []; }
            }));
            var winners = allocations.SelectMany(rows => rows).ToArray();
            Assert.Equal(4, winners.Length);
            Assert.Equal(4, winners.Select(row => row.AgentId).Distinct().Count());
            Assert.All(winners, row => Assert.True(identities.Add(row.Id)));
            Assert.Equal(4L, await ActiveReservations());
            await Task.WhenAll(winners.SelectMany(row => new Task[]
            {
                Leases.RenewAsync(row.RunId, row.NodeKey, Token),
                Leases.ReleaseAsync(row.RunId, row.NodeKey, "Race release", Token)
            }));
            Assert.Equal(4L, await ActiveReservations());
            using var rebuiltRegistry = new AgentRegistry(database.DataSource, clock);
            await rebuiltRegistry.LoadAsync(Token);
            using var rebuilt = new ReservationLeaseService(database.DataSource, Store, clock, NullLogger<ReservationLeaseService>.Instance);
            await Task.WhenAll(winners.Select(async row =>
            {
                var session = new AgentSession(row.AgentId, rebuiltRegistry.Find(row.AgentId)!.SessionId);
                var fence = Assert.Single(await rebuilt.PendingAsync(session, Token));
                Assert.True(fence.ReservationEpoch > (ulong)row.Epoch);
                Assert.Equal(fence.ReservationEpoch, Assert.Single(await rebuilt.PendingAsync(session, Token)).ReservationEpoch);
                await rebuilt.ConfirmAsync(session, fence.ReservationEpoch, Token);
            }));
            Assert.Equal(0L, await ActiveReservations());
            pending.RemoveAll(run => winners.Any(row => row.RunId == run.Snapshot.RunId));
        }
        Assert.Empty(pending);
        Assert.Equal(20, identities.Count);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM reservations WHERE run_id=ANY($1) AND released_at IS NOT NULL");
        count.Parameters.AddWithValue(all.ToArray());
        Assert.Equal(20L, await count.ExecuteScalarAsync(Token));

        async Task<long> ActiveReservations()
        {
            await using var query = database.DataSource.CreateCommand("""
                SELECT count(*) FROM reservations WHERE run_id=ANY($1) AND released_at IS NULL
                """);
            query.Parameters.AddWithValue(all.ToArray());
            return (long)(await query.ExecuteScalarAsync(Token))!;
        }
    }
}
