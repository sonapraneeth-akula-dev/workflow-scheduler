using Wf.Contracts;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class ScheduleFireTests
{
    [Theory]
    [InlineData(1, 1L)]
    [InlineData(0, 0L)]
    public async Task ScheduleCatchUp_records_every_missed_slot_and_links_only_one_newest_source_without_a_second_fire_row(
        short policy, long expectedRuns)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await Execute("UPDATE schedules SET catchup_policy=$2 WHERE id=$1", schedule.Id, policy);
        server.Clock.Now = schedule.Timer.FireAt.AddMinutes(9).AddSeconds(30);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var next = (await engine.Scheduler.FireAsync(schedule.Timer, Token))!;
        Assert.Equal(schedule.Timer.FireAt.AddMinutes(10), next.FireAt);
        Assert.Equal(10L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", schedule.Id));
        Assert.Equal(10L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(expectedRuns, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(expectedRuns, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND run_id IS NOT NULL", schedule.Id));
        if (expectedRuns == 1)
        {
            var source = schedule.Timer.FireAt.AddMinutes(9);
            var run = Assert.IsType<Guid>(await Scalar("SELECT run_id FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2",
                schedule.Id, source));
            Assert.Equal((short)2, await Scalar("SELECT trigger FROM runs WHERE id=$1", run));
            Assert.Equal("catch-up", await Scalar("SELECT reason FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2", schedule.Id, source));
            Assert.Equal(schedule.Version, await Scalar("SELECT version_id FROM runs WHERE id=$1", run));
            Assert.Equal("first", (await engine.Store.LoadAsync(run, Token)).Snapshot.Variables["env"].GetString());
            Assert.Equal(server.Clock.Now, await Scalar("SELECT created_at FROM runs WHERE id=$1", run));
        }
        Assert.Equal(next, await engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.Equal(expectedRuns, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(10L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
    }

    [Theory]
    [InlineData("Skip", 1L, 0L, (short)1)]
    [InlineData("Queue", 2L, 1L, (short)0)]
    [InlineData("Concurrent", 2L, 1L, (short)0)]
    public async Task ScheduleCatchUp_latest_action_precedes_recent_normal_fires_and_both_obey_overlap(
        string overlap, long totalRuns, long scheduledRuns, short recentOutcome)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server, overlap);
        server.Clock.Now = schedule.Timer.FireAt.AddMinutes(9).AddSeconds(1);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await engine.Scheduler.FireAsync(schedule.Timer, Token);
        Assert.Equal(9L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", schedule.Id));
        Assert.Equal(10L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
        Assert.Equal(scheduledRuns, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=1", schedule.Series));
        Assert.Equal(totalRuns, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(recentOutcome, await Scalar("SELECT outcome FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2",
            schedule.Id, schedule.Timer.FireAt.AddMinutes(9)));
    }

    [Theory]
    [InlineData(0, (short)0, (short)1)]
    [InlineData(1, (short)2, (short)2)]
    public async Task ScheduleCatchUp_exact_grace_boundary_starts_normally_but_one_microsecond_later_is_caught_up(
        int lateMicroseconds, short outcome, short trigger)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        server.Clock.Now = schedule.Timer.FireAt.AddSeconds(2).AddMicroseconds(lateMicroseconds);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await engine.Scheduler.FireAsync(schedule.Timer, Token);
        Assert.Equal(outcome, await Scalar("SELECT outcome FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(trigger, await Scalar("SELECT trigger FROM runs WHERE series_id=$1", schedule.Series));
    }

    [Theory]
    [InlineData("Skip", 1L, 0L)]
    [InlineData("Queue", 2L, 1L)]
    [InlineData("Concurrent", 2L, 1L)]
    public async Task ScheduleCatchUp_existing_nonterminal_run_does_not_bypass_the_instance_overlap_policy(
        string overlap, long totalRuns, long catchUpRuns)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server, overlap);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var next = (await engine.Scheduler.FireAsync(schedule.Timer, Token))!;
        server.Clock.Now = schedule.Timer.FireAt.AddMinutes(9).AddSeconds(30);
        await engine.Scheduler.FireAsync(next, Token);
        Assert.Equal(9L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", schedule.Id));
        Assert.Equal(totalRuns, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(catchUpRuns, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
        if (overlap == "Skip")
            Assert.Equal("overlap", await Scalar("SELECT reason FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2",
                schedule.Id, schedule.Timer.FireAt.AddMinutes(9)));
    }

    [Fact]
    public async Task ScheduleCatchUp_none_records_misses_without_suppressing_a_recent_normal_fire()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await Execute("UPDATE schedules SET catchup_policy=0 WHERE id=$1", schedule.Id);
        server.Clock.Now = schedule.Timer.FireAt.AddMinutes(9).AddSeconds(1);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await engine.Scheduler.FireAsync(schedule.Timer, Token);
        Assert.Equal(9L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", schedule.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=0", schedule.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=1", schedule.Series));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
    }

    [Theory]
    [InlineData(0, 1L)]
    [InlineData(9, 0L)]
    public async Task ScheduleCatchUp_preclaimed_history_is_not_rewritten_and_a_claimed_latest_source_never_starts_twice(
        int existingSlot, long expectedRuns)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        var persisted = schedule.Timer.FireAt.AddMinutes(existingSlot);
        await Execute("INSERT INTO schedule_fires(schedule_id,fire_at,outcome,reason) VALUES($1,$2,2,'original history')",
            schedule.Id, persisted);
        server.Clock.Now = schedule.Timer.FireAt.AddMinutes(9).AddSeconds(30);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await engine.Scheduler.FireAsync(schedule.Timer, Token);
        Assert.Equal(10L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", schedule.Id));
        Assert.Equal(expectedRuns, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal("original history", await Scalar("SELECT reason FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2",
            schedule.Id, persisted));
    }

    [Fact]
    public async Task ScheduleCatchUp_validation_failure_rolls_back_every_miss_and_the_final_cursor_before_retry()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        server.Clock.Now = schedule.Timer.FireAt.AddMinutes(9).AddSeconds(30);
        await Execute("UPDATE schedules SET variables='{\"unknown\":\"never-echo-this-value\"}' WHERE id=$1", schedule.Id);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var failure = await Assert.ThrowsAsync<Wf.ControlPlane.Engine.Scheduler.ScheduleFireRejectedException>(
            () => engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.DoesNotContain("never-echo-this-value", failure.Message);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(schedule.Timer.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        await Execute("UPDATE schedules SET variables='{\"env\":\"repaired\"}' WHERE id=$1", schedule.Id);
        await engine.Scheduler.FireAsync(schedule.Timer, Token);
        Assert.Equal(10L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", schedule.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
    }

    [Fact]
    public async Task ScheduleCatchUp_ten_minute_hosted_outage_and_repeated_leader_takeover_create_exactly_one_catch_up_run()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using (var first = new Engine(database.DataSource, server))
        {
            await first.Start();
            await WaitFor(async () => (long)(await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series))! == 1);
            var run = Assert.IsType<Guid>(await Scalar("SELECT id FROM runs WHERE series_id=$1", schedule.Series));
            await first.Store.AppendAsync(await first.Store.LoadAsync(run, Token), Wf.Contracts.Events.EventEntity.Run,
                null, (int)RunStatus.Aborted, server.Clock.Now, Token);
        }
        server.Clock.Now = schedule.Timer.FireAt.AddMinutes(10).AddSeconds(30);
        await using (var replacement = new Engine(database.DataSource, server))
        {
            await replacement.Start();
            await WaitFor(async () => (long)(await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series))! == 1);
            Assert.Equal(10L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", schedule.Id));
            Assert.Equal(schedule.Timer.FireAt.AddMinutes(11), await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        }
        await using var nextLeader = new Engine(database.DataSource, server);
        await nextLeader.Start();
        await WaitFor(() => nextLeader.Scheduler.IsReady);
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
        Assert.Equal(11L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2 AND run_id IS NOT NULL", schedule.Id));
        var catchUp = Assert.IsType<Guid>(await Scalar("SELECT id FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
        Assert.True(nextLeader.Recovery.RecoveredRuns.ContainsKey(catchUp));
    }
}
