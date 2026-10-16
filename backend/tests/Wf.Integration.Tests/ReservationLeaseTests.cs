using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Resources;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Storage;

namespace Wf.Integration.Tests;

public sealed partial class ReservationManagerTests
{
    private ReservationLeaseService Leases => new(database.DataSource, Store, clock, NullLogger<ReservationLeaseService>.Instance);

    [Fact]
    public async Task Release_failure_rolls_back_cancellation_outbox_epochs_and_the_entire_machine_cohort()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 2);
        var run = await Ready();
        var cohort = await Manager.ReserveAsync(run.Snapshot.RunId, "work", 2, new(pool: Pool(fleet)), Lifetime, Token);
        using var dispatcher = new Dispatcher(database.DataSource, Store, registry, new(), clock);
        await Send(dispatcher, run, "work", "first", new(machineIds: [fleet[0].AgentId]));
        var sequence = run.Snapshot.LastSequence;
        await Execute($"ALTER TABLE reservations ADD CONSTRAINT reject_release_test CHECK (id <> '{cohort[^1].Id:D}' OR fence_epoch IS NULL)");
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Leases.ReleaseAsync(run.Snapshot.RunId, "work", "Atomic rollback", Token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal(sequence, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
            Assert.Single(await Store.PendingAsync(fleet[0], Token));
            foreach (var reservation in cohort)
            {
                var row = await ReservationRow(reservation.Id);
                Assert.False(row.Closing);
                Assert.False(row.Released);
                Assert.Equal(0, row.Fence);
                await using var epoch = database.DataSource.CreateCommand("SELECT reservation_epoch FROM agents WHERE id=$1");
                epoch.Parameters.AddWithValue(reservation.AgentId);
                Assert.Equal(reservation.Epoch, await epoch.ExecuteScalarAsync(Token));
            }
        }
        finally { await Execute("ALTER TABLE reservations DROP CONSTRAINT reject_release_test"); }
        await Leases.ReleaseAsync(run.Snapshot.RunId, "work", "Release after rollback", Token);
        foreach (var session in fleet)
        {
            var pending = Assert.Single(await Leases.PendingAsync(session, Token));
            await Leases.ConfirmAsync(session, pending.ReservationEpoch, Token);
        }
        foreach (var reservation in cohort) Assert.True((await ReservationRow(reservation.Id)).Released);
    }

    [Fact]
    public async Task Malformed_renewal_release_and_fence_inputs_fail_explicitly_without_state_changes()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var run = await Ready();
        var reservation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Leases.RenewAsync(Guid.NewGuid(), "work", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Leases.RenewAsync(run.Snapshot.RunId, "work/first", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Leases.RenewAsync(run.Snapshot.RunId, "missing", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Leases.ReleaseAsync(run.Snapshot.RunId, "work", " ", Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Leases.ReleaseAsync(run.Snapshot.RunId, "work", new('x', 501), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Leases.ConfirmAsync(fleet[0], 0, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Leases.ConfirmAsync(fleet[0], ulong.MaxValue, Token));
        Assert.False(await Leases.RenewAsync(run.Snapshot.RunId, "other", Token));
        var row = await ReservationRow(reservation.Id);
        Assert.Equal(reservation.ExpiresAt, row.Expires);
        Assert.False(row.Closing);
        Assert.False(row.Released);
    }

    [Fact]
    public async Task Renewal_extends_the_original_lifetime_without_replacing_identity_epoch_or_selector()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var run = await Ready();
        var original = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        clock.Now = original.RenewedAt;
        clock.Now += TimeSpan.FromSeconds(29);
        await Leases.SweepAsync(Token);
        Assert.Equal(original.ExpiresAt, (await ReservationRow(original.Id)).Expires);
        clock.Now += TimeSpan.FromSeconds(1);
        await Leases.SweepAsync(Token);
        var renewed = await ReservationRow(original.Id);
        Assert.Equal(clock.Now + Lifetime, renewed.Expires);
        Assert.Equal(original.Epoch, renewed.Epoch);
        Assert.Equal(0, renewed.Fence);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await Leases.RenewAsync(run.Snapshot.RunId, "work", Token));
        renewed = await ReservationRow(original.Id);
        Assert.Equal(clock.Now + Lifetime, renewed.Expires);
        Assert.Equal(original.Epoch, renewed.Epoch);
        Assert.Equal(1L, await Count(run.Snapshot.RunId));
    }

    [Theory]
    [InlineData("node-completed")]
    [InlineData("node-failed")]
    [InlineData("run-completed")]
    [InlineData("run-failed")]
    [InlineData("abort")]
    [InlineData("expired")]
    [InlineData("explicit")]
    public async Task Ended_or_expired_owners_cancel_before_requesting_a_physical_fence_and_release_only_after_its_ack(string cause)
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var run = await Ready();
        var reservation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        using var dispatcher = new Dispatcher(database.DataSource, Store, registry, new(), clock);
        Assert.NotNull((await Send(dispatcher, run, "work", "first", new(machineIds: [fleet[0].AgentId]))).Agent);
        var command = Assert.Single(await Store.PendingAsync(fleet[0], Token)).Command;
        await Store.AcceptAsync(fleet[0], new() { TaskStarted = new()
            { Task = command.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.Now), ReservationEpoch = command.ReservationEpoch } }, Token);
        run = await Store.LoadAsync(run.Snapshot.RunId, Token);
        if (cause.StartsWith("node-", StringComparison.Ordinal))
        {
            foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running,
                         cause == "node-completed" ? ExecutionStatus.Succeeded : ExecutionStatus.Failed })
                await Store.AppendAsync(run, EventEntity.Node, "work", (int)status, clock.Now, Token);
        }
        if (cause.StartsWith("run-", StringComparison.Ordinal))
            await Store.AppendAsync(run, EventEntity.Run, null,
                (int)(cause == "run-completed" ? RunStatus.Succeeded : RunStatus.Failed), clock.Now, Token);
        if (cause == "abort")
            await Store.AppendAsync(run, run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Aborting, clock.Now,
                new("operator", "Operator", clock.Now, "Stop test", AbortSource.CLI)), Token);
        if (cause == "expired") clock.Now = reservation.ExpiresAt;
        var before = run.Snapshot.LastSequence;
        if (cause == "explicit") await Leases.ReleaseAsync(run.Snapshot.RunId, "work", "Manual release", Token);
        else await Leases.SweepAsync(Token);
        var row = await ReservationRow(reservation.Id);
        Assert.True(row.Closing);
        Assert.False(row.Released);
        Assert.True(row.Fence > reservation.Epoch);
        Assert.False(await Leases.RenewAsync(run.Snapshot.RunId, "work", Token));
        var closingSequence = (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence;
        if (cause.StartsWith("run-", StringComparison.Ordinal)) Assert.Equal(before, closingSequence);
        else Assert.Equal(ExecutionStatus.Cancelled,
            (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/first"]);
        Assert.Empty(await Store.PendingAsync(fleet[0], Token));
        Assert.Equal((long)ExecutionStatus.Cancelled, await Scalar(
            "SELECT status::bigint FROM task_attempts WHERE run_id=$1 AND task_key='work/first'", run.Snapshot.RunId));
        await Store.AcceptAsync(fleet[0], new() { TaskCompleted = new()
        {
            Task = command.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.Now),
            Outcome = TaskOutcome.Succeeded, OutputsJson = ByteString.CopyFromUtf8("""{"stale":true}"""),
            ReservationEpoch = command.ReservationEpoch
        } }, Token);
        Assert.Equal(closingSequence, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        await Leases.SweepAsync(Token);
        Assert.Equal(row.Fence, (await ReservationRow(reservation.Id)).Fence);
        var pending = Assert.Single(await Leases.PendingAsync(fleet[0], Token));
        Assert.Equal((ulong)row.Fence, pending.ReservationEpoch);
        await Leases.ConfirmAsync(fleet[0], pending.ReservationEpoch, Token);
        await Leases.ConfirmAsync(fleet[0], pending.ReservationEpoch, Token);
        Assert.True((await ReservationRow(reservation.Id)).Released);
        Assert.Empty(await Leases.PendingAsync(fleet[0], Token));
        Assert.True(await registry.HeartbeatAsync(fleet[0], new() { At = Timestamp.FromDateTimeOffset(clock.Now) }, Token));
        var foreign = await Ready();
        var reuse = await Send(dispatcher, foreign, "work", "first", new(machineIds: [fleet[0].AgentId]));
        Assert.NotNull(reuse.Agent);
        Assert.Equal((ulong)row.Fence, Assert.Single(await Store.PendingAsync(fleet[0], Token)).Command.ReservationEpoch);
    }

    [Fact]
    public async Task Disconnected_agent_remains_quarantined_and_only_current_session_exact_fence_can_release_it()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var run = await Ready();
        var allocation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Leases.ConfirmAsync(fleet[0], (ulong)allocation.Epoch, Token));
        clock.Now = allocation.ExpiresAt;
        await Leases.SweepAsync(Token);
        var fence = Assert.Single(await Leases.PendingAsync(fleet[0], Token)).ReservationEpoch;
        var other = await Ready();
        await Assert.ThrowsAsync<ReservationUnavailableException>(() =>
            Manager.ReserveAsync(other.Snapshot.RunId, "work", 1, new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        using var dispatcher = new Dispatcher(database.DataSource, Store, registry, new(), clock);
        Assert.Null((await Send(dispatcher, other, "work", "first", new(machineIds: [fleet[0].AgentId]))).Agent);
        var reconnected = await registry.RegisterAsync(Hello(fleet[0].AgentId, Pool(fleet), "windows"), Token);
        Assert.Empty(await Leases.PendingAsync(fleet[0], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Leases.ConfirmAsync(fleet[0], fence, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Leases.ConfirmAsync(reconnected, fence + 1, Token));
        Assert.False((await ReservationRow(allocation.Id)).Released);
        Assert.Equal(fence, Assert.Single(await Leases.PendingAsync(reconnected, Token)).ReservationEpoch);
        await Leases.ConfirmAsync(reconnected, fence, Token);
        Assert.True((await ReservationRow(allocation.Id)).Released);
        Assert.NotEmpty(await Manager.ReserveAsync(other.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        await Leases.ConfirmAsync(reconnected, fence, Token);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL", other.Snapshot.RunId));
    }

    [Fact]
    public async Task Expiry_and_release_discard_late_resource_samples_without_uploading_blobs_or_changing_aggregates()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var run = await Ready();
        var reservation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        using var dispatcher = new Dispatcher(database.DataSource, Store, registry, new(), clock);
        await Send(dispatcher, run, "work", "first", new(machineIds: [fleet[0].AgentId]));
        var command = Assert.Single(await Store.PendingAsync(fleet[0], Token)).Command;
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "reservation-resources", Guid.NewGuid().ToString("N")));
        using var blobs = new FileSystemBlobStore(root, RandomNumberGenerator.GetBytes(32));
        try
        {
            var sink = new ResourceSampleSink(database.DataSource, Store, blobs);
            var point = new ResourcePoint(clock.Now, null, null, 42, null, null, null, null, null, null,
                "process", "process-rss", "unavailable", "First sample", null, "No counter", "First sample");
            var sample = new ResourceSample
            {
                Task = command.Task.Clone(), ReservationEpoch = command.ReservationEpoch, SampleSequence = 1,
                SampleJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                    new ResourceObservation(point, null, "Unavailable in fixture"), ResourceJsonContext.Default.ResourceObservation))
            };
            clock.Now = reservation.ExpiresAt;
            await sink.AcceptAsync(fleet[0], sample, Token);
            await Leases.SweepAsync(Token);
            await Leases.ConfirmAsync(fleet[0], Assert.Single(await Leases.PendingAsync(fleet[0], Token)).ReservationEpoch, Token);
            await sink.AcceptAsync(fleet[0], sample, Token);
            Assert.Equal(0L, await Scalar("SELECT resource_sample_count FROM task_attempts WHERE run_id=$1", run.Snapshot.RunId));
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Concurrent_release_and_renewals_never_revive_a_closing_lease_or_allocate_multiple_fences()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var run = await Ready();
        var allocation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
        {
            if (index % 2 == 0) await Leases.ReleaseAsync(run.Snapshot.RunId, "work", "Concurrent release", Token);
            else await Leases.RenewAsync(run.Snapshot.RunId, "work", Token);
        }));
        var row = await ReservationRow(allocation.Id);
        Assert.True(row.Closing);
        Assert.False(row.Released);
        Assert.True(row.Fence > row.Epoch);
        Assert.Single(await Leases.PendingAsync(fleet[0], Token));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Leases.ConfirmAsync(fleet[0], (ulong)row.Fence, Token)));
        Assert.True((await ReservationRow(allocation.Id)).Released);
    }

    [Fact]
    public async Task Former_leader_cannot_renew_release_or_accept_a_physical_fence_after_lease_takeover()
    {
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry, 1);
        var run = await Ready();
        var reservation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(machineIds: [fleet[0].AgentId]), Lifetime, Token));
        await Leases.ReleaseAsync(run.Snapshot.RunId, "work", "Leadership test", Token);
        var fence = Assert.Single(await Leases.PendingAsync(fleet[0], Token)).ReservationEpoch;
        using var leadership = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"),
            new("http://localhost:5544")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leadership.TryAcquireOrRenewAsync(Token));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leadership, clock);
            using var service = new ReservationLeaseService(database.DataSource, store, clock, NullLogger<ReservationLeaseService>.Instance);
            await Execute("UPDATE leases SET epoch=epoch+1,holder='replacement' WHERE name='engine'");
            await Assert.ThrowsAsync<NotLeaderException>(() => service.RenewAsync(run.Snapshot.RunId, "work", Token));
            await Assert.ThrowsAsync<NotLeaderException>(() => service.ReleaseAsync(run.Snapshot.RunId, "work", "Stale", Token));
            await Assert.ThrowsAsync<NotLeaderException>(() => service.ConfirmAsync(fleet[0], fence, Token));
            Assert.False((await ReservationRow(reservation.Id)).Released);
        }
        finally { await Execute("DELETE FROM leases WHERE name='engine'"); }
    }

    private async Task<(long Epoch, DateTimeOffset Expires, bool Closing, long Fence, bool Released)> ReservationRow(Guid id)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT epoch,expires_at,release_requested_at IS NOT NULL,COALESCE(fence_epoch,0),released_at IS NOT NULL
            FROM reservations WHERE id=$1
            """);
        query.Parameters.AddWithValue(id);
        await using var reader = await query.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return (reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetBoolean(2), reader.GetInt64(3), reader.GetBoolean(4));
    }
}
