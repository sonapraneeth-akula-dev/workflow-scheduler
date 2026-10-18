using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core.Placement;

namespace Wf.Integration.Tests;

public sealed partial class ReservationManagerTests
{
    [Fact]
    public async Task Rejection_failure_rolls_back_run_events_snapshot_attempts_and_other_node_queue_intent()
    {
        using var telemetry = new TelemetryMeasurements("tests/perf/reservations");
        var run = await Ready();
        var selector = new PlacementSelector(pool: Guid.NewGuid().ToString("N"));
        await Manager.RequestAsync(run.Snapshot.RunId, "other", 1, selector, Lifetime, cancellationToken: Token);
        var sequence = run.Snapshot.LastSequence;
        await Execute($"""
            ALTER TABLE runs ADD CONSTRAINT reject_shortage_failure_test CHECK (id <> '{run.Snapshot.RunId:D}' OR status<>4)
            """);
        try
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Manager.RequestAsync(run.Snapshot.RunId,
                "work", 1, selector, Lifetime, WhenUnavailable.Reject, Token));
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, error.SqlState);
            var durable = await Store.LoadAsync(run.Snapshot.RunId, Token);
            Assert.Equal(RunStatus.Running, durable.Snapshot.Status);
            Assert.Equal(sequence, durable.Snapshot.LastSequence);
            Assert.Null(durable.Snapshot.FailureReason);
            Assert.All(durable.Snapshot.Tasks.Values, status => Assert.Equal(ExecutionStatus.Ready, status));
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", run.Snapshot.RunId));
            Assert.Empty(telemetry.Values("wf.run.completed"));
        }
        finally { await Execute("ALTER TABLE runs DROP CONSTRAINT reject_shortage_failure_test"); }
        Assert.Equal(ReservationDisposition.Rejected, (await Manager.RequestAsync(run.Snapshot.RunId,
            "work", 1, selector, Lifetime, WhenUnavailable.Reject, Token)).Disposition);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", run.Snapshot.RunId));
        Assert.Equal([1d], telemetry.Values("wf.run.completed"));
    }

    [Fact]
    public async Task Queued_requests_keep_FIFO_positions_across_retries_and_manager_restart_until_physical_fence()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var selector = new PlacementSelector(pool: Pool(fleet));
        var owner = await Ready();
        var first = await Ready();
        var second = await Ready();
        await Manager.ReserveAsync(owner.Snapshot.RunId, "work", 1, selector, Lifetime, Token);
        Assert.Equal(1, (await Manager.RequestAsync(first.Snapshot.RunId, "work", 1, selector, Lifetime,
            cancellationToken: Token)).Position);
        Assert.Equal(2, (await Manager.RequestAsync(second.Snapshot.RunId, "work", 1, selector, Lifetime,
            cancellationToken: Token)).Position);
        Assert.Equal(1, (await Manager.RequestAsync(first.Snapshot.RunId, "work", 1, selector, Lifetime,
            cancellationToken: Token)).Position);
        var rebuilt = new ReservationManager(database.DataSource, Store, clock);
        var leases = new ReservationLeaseService(database.DataSource, Store, clock, NullLogger<ReservationLeaseService>.Instance);
        await leases.ReleaseAsync(owner.Snapshot.RunId, "work", "FIFO integration release", Token);
        await rebuilt.SweepQueueAsync(Token);
        Assert.Equal(0, await Count(first.Snapshot.RunId));
        var fence = Assert.Single(await leases.PendingAsync(fleet[0], Token));
        await leases.ConfirmAsync(fleet[0], fence.ReservationEpoch, Token);
        await rebuilt.SweepQueueAsync(Token);
        Assert.Equal(1, await Count(first.Snapshot.RunId));
        Assert.Equal(0, await Count(second.Snapshot.RunId));
        Assert.Equal(1, (await rebuilt.RequestAsync(second.Snapshot.RunId, "work", 1, selector, Lifetime,
            cancellationToken: Token)).Position);
        var original = Assert.Single((await rebuilt.RequestAsync(first.Snapshot.RunId, "work", 1, selector, Lifetime,
            cancellationToken: Token)).Reservations);
        Assert.Equal(original, Assert.Single((await rebuilt.RequestAsync(first.Snapshot.RunId, "work", 1, selector, Lifetime,
            cancellationToken: Token)).Reservations));
        await leases.ReleaseAsync(first.Snapshot.RunId, "work", "Next waiter", Token);
        await leases.ConfirmAsync(fleet[0], Assert.Single(await leases.PendingAsync(fleet[0], Token)).ReservationEpoch, Token);
        await rebuilt.SweepQueueAsync(Token);
        Assert.Equal(1, await Count(second.Snapshot.RunId));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", first.Snapshot.RunId));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", second.Snapshot.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reject_shortage_commits_failed_run_reason_events_and_no_reservation_or_queue(bool queuedRun)
    {
        using var telemetry = new TelemetryMeasurements("tests/perf/reservations");
        var run = await Ready();
        if (queuedRun)
        {
            var snapshot = run.Snapshot with { Status = RunStatus.Queued, LastSequence = 0 };
            run = Wf.Engine.Core.RunState.Restore(snapshot with { RunId = Guid.CreateVersion7(clock.Now) }, [], Assert.Fail);
            await Store.CreateAsync(run, "tests/perf/reservations", Guid.NewGuid(), clock.Now.AddHours(1), Token);
        }
        var result = await Manager.RequestAsync(run.Snapshot.RunId, "work", 2,
            new(pool: Guid.NewGuid().ToString("N")), Lifetime, WhenUnavailable.Reject, Token);
        Assert.Equal(ReservationDisposition.Rejected, result.Disposition);
        Assert.Equal("ReservationUnavailable", result.Reason);
        Assert.Null(result.Position);
        Assert.Empty(result.Reservations);
        var durable = await Store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(RunStatus.Failed, durable.Snapshot.Status);
        Assert.Equal("ReservationUnavailable", durable.Snapshot.FailureReason);
        Assert.Equal(ExecutionStatus.Failed, durable.Snapshot.Nodes["work"]);
        Assert.All(durable.Snapshot.Tasks.Values, status => Assert.Equal(ExecutionStatus.Cancelled, status));
        Assert.Equal(0, await Count(run.Snapshot.RunId));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", run.Snapshot.RunId));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM run_events WHERE run_id=$1 AND kind=0 AND payload IS NOT NULL AND seq=(SELECT max(seq) FROM run_events WHERE run_id=$1)", run.Snapshot.RunId));
        Assert.Equal([1d], telemetry.Values("wf.run.completed"));
    }

    [Fact]
    public async Task Queue_retry_cannot_change_count_selector_lifetime_or_policy()
    {
        var run = await Ready();
        var selector = new PlacementSelector(pool: Guid.NewGuid().ToString("N"));
        await Manager.RequestAsync(run.Snapshot.RunId, "work", 1, selector, Lifetime, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.RequestAsync(run.Snapshot.RunId, "work", 2, selector, Lifetime, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.RequestAsync(run.Snapshot.RunId, "work", 1, new(pool: "changed"), Lifetime, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.RequestAsync(run.Snapshot.RunId, "work", 1, selector, Lifetime + Lifetime, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager.RequestAsync(run.Snapshot.RunId, "work", 1, selector, Lifetime, WhenUnavailable.Reject, Token));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", run.Snapshot.RunId));
        await Store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Failed, clock.Now, Token);
        await Manager.SweepQueueAsync(Token);
    }

    [Fact]
    public async Task Available_later_requests_and_low_level_reserves_cannot_jump_an_unsatisfied_head()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var first = await Ready();
        var second = await Ready();
        var rejected = await Ready();
        var selector = new PlacementSelector(pool: Pool(fleet));
        await Manager.RequestAsync(first.Snapshot.RunId, "work", 2, selector, Lifetime, cancellationToken: Token);
        Assert.Equal(2, (await Manager.RequestAsync(second.Snapshot.RunId, "work", 1, selector, Lifetime,
            cancellationToken: Token)).Position);
        await Assert.ThrowsAsync<ReservationUnavailableException>(() =>
            Manager.ReserveAsync(second.Snapshot.RunId, "other", 1, selector, Lifetime, Token));
        Assert.Equal(ReservationDisposition.Rejected, (await Manager.RequestAsync(rejected.Snapshot.RunId, "work", 1,
            selector, Lifetime, WhenUnavailable.Reject, Token)).Disposition);
        Assert.Equal(0, await Count(second.Snapshot.RunId));
        await Store.AppendAsync(first, EventEntity.Run, null, (int)RunStatus.Failed, clock.Now, Token);
        await Manager.SweepQueueAsync(Token);
        Assert.Equal(1, await Count(second.Snapshot.RunId));
    }

    [Fact]
    public async Task Twenty_simultaneous_queue_requests_for_four_machines_get_four_owners_and_sixteen_unique_positions()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 4);
        var runs = new List<Wf.Engine.Core.RunState>();
        for (var index = 0; index < 20; index++) runs.Add(await Ready());
        var results = await Task.WhenAll(runs.Select(run => Manager.RequestAsync(run.Snapshot.RunId, "work", 1,
            new(pool: Pool(fleet)), Lifetime, cancellationToken: Token)));
        Assert.Equal(4, results.Count(result => result.Disposition == ReservationDisposition.Reserved));
        Assert.Equal(Enumerable.Range(1, 16), results.Where(result => result.Position is not null)
            .Select(result => result.Position!.Value).Order());
        Assert.Equal(4, results.SelectMany(result => result.Reservations).Select(row => row.AgentId).Distinct().Count());
        foreach (var run in runs)
        {
            var duplicate = await Manager.RequestAsync(run.Snapshot.RunId, "work", 1, new(pool: Pool(fleet)), Lifetime,
                cancellationToken: Token);
            Assert.True(duplicate.Reservations.Count == 1 ^ duplicate.Position is not null);
        }
        // End the waiters so independent test cases sharing this fixture cannot inherit a live head.
        foreach (var run in runs.Where(run => results[runs.IndexOf(run)].Disposition == ReservationDisposition.Queued))
            await Store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Failed, clock.Now, Token);
        await Manager.SweepQueueAsync(Token);
    }

    [Fact]
    public async Task Invalid_queue_policy_and_inputs_leave_no_durable_intent()
    {
        var run = await Ready();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Manager.RequestAsync(run.Snapshot.RunId, "work", 1,
            new(), Lifetime, (WhenUnavailable)100, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Manager.RequestAsync(run.Snapshot.RunId, "work", 0,
            new(), Lifetime, cancellationToken: Token));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM reservation_queue WHERE run_id=$1", run.Snapshot.RunId));
        Assert.Empty(MigrationLint.Validate(Migrator.LoadScripts().Single(script => script.Name == "0030_reservation_queue.sql").Contents));
    }
}
