using Wf.Contracts;
using Wf.Contracts.Events;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class ScheduleFireTests
{
    [Fact]
    public async Task MissedFireCatchUp_ten_minute_multi_schedule_outage_preserves_every_slot_and_exactly_one_latest_run_across_leaders()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var latest = new[] { await CreateSchedule(server), await CreateSchedule(server), await CreateSchedule(server) };
        var none = new[] { await CreateSchedule(server), await CreateSchedule(server) };
        var paused = await CreateSchedule(server);
        var removed = await CreateSchedule(server);
        var all = latest.Concat(none).Append(paused).Append(removed).ToArray();
        var ids = all.Select(schedule => schedule.Id).ToArray();
        var firstFire = new DateTimeOffset(server.Clock.Now.UtcTicks / 10 * 10, TimeSpan.Zero);
        server.Clock.Now = firstFire;
        await Execute("UPDATE schedules SET next_fire_at=$2 WHERE id=ANY($1)", ids, firstFire);
        await Execute("UPDATE schedules SET catchup_policy=0 WHERE id=ANY($1)", none.Select(schedule => schedule.Id).ToArray());
        await using (var initial = new Engine(database.DataSource, server))
        {
            await initial.Start();
            await WaitFor(async () => (long)(await Scalar("""
                SELECT count(*) FROM schedule_fires
                WHERE schedule_id=ANY($1) AND fire_at=$2 AND outcome=0 AND run_id IS NOT NULL
                """, ids, firstFire))! == all.Length);
            foreach (var schedule in all)
            {
                var run = Assert.IsType<Guid>(await Scalar("SELECT run_id FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2",
                    schedule.Id, firstFire));
                await initial.Store.AppendAsync(await initial.Store.LoadAsync(run, Token), EventEntity.Run,
                    null, (int)RunStatus.Aborted, server.Clock.Now, Token);
            }
        }
        await Execute("UPDATE schedules SET paused=true,next_fire_at=NULL WHERE id=$1", paused.Id);
        await Execute("UPDATE schedules SET deleted_at=now(),paused=true,next_fire_at=NULL WHERE id=$1", removed.Id);
        // Advance a stopped scheduler beyond ten minutes and the two-second fire grace.
        server.Clock.Now = firstFire.AddMinutes(10).AddSeconds(3);
        var nextFire = firstFire.AddMinutes(11);
        await using (var replacement = new Engine(database.DataSource, server))
        {
            await replacement.Start();
            await WaitFor(async () => (long)(await Scalar("""
                SELECT count(*) FROM schedules WHERE id=ANY($1) AND next_fire_at=$2
                """, latest.Concat(none).Select(schedule => schedule.Id).ToArray(), nextFire))! == latest.Length + none.Length);
            foreach (var schedule in latest.Concat(none))
            {
                var catchesUp = latest.Contains(schedule);
                var history = await ReadFires(server, schedule.Series);
                Assert.Null(history.NextCursor);
                Assert.Equal(11, history.Items.Length);
                Assert.Equal(Enumerable.Range(0, 11).Reverse().Select(minute => firstFire.AddMinutes(minute)),
                    history.Items.Select(fire => fire.FireAt));
                Assert.All(history.Items.Take(10), fire => Assert.Equal("Missed", fire.Outcome));
                Assert.Equal("Started", history.Items[^1].Outcome);
                var linked = history.Items.Take(10).Where(fire => fire.RunId is not null).ToArray();
                Assert.Equal(catchesUp ? 1 : 0, linked.Length);
                Assert.Equal(catchesUp ? 2L : 1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
                Assert.Equal(catchesUp ? 1L : 0L,
                    await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
                if (catchesUp)
                {
                    var source = Assert.Single(linked);
                    Assert.Equal(firstFire.AddMinutes(10), source.FireAt);
                    Assert.Equal("catch-up", source.Reason);
                    var runId = source.RunId!.Value;
                    var run = await replacement.Store.LoadAsync(runId, Token);
                    Assert.Equal("first", run.Snapshot.Variables["env"].GetString());
                    Assert.Equal(schedule.Version, await Scalar("SELECT version_id FROM runs WHERE id=$1", runId));
                    Assert.Equal(server.Clock.Now, await Scalar("SELECT created_at FROM runs WHERE id=$1", runId));
                }
            }
            foreach (var schedule in new[] { paused, removed })
            {
                Assert.Single((await ReadFires(server, schedule.Series)).Items);
                Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
                Assert.Equal(DBNull.Value, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
            }
        }
        await using var third = new Engine(database.DataSource, server);
        await third.Start();
        await WaitFor(() => third.Scheduler.IsReady);
        foreach (var schedule in latest.Concat(none))
        {
            await third.Scheduler.FireAsync(new(schedule.Id, firstFire.AddMinutes(1)), Token);
            Assert.Equal(11L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
            Assert.Equal(latest.Contains(schedule) ? 1L : 0L,
                await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
            Assert.Equal(nextFire, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
            if (latest.Contains(schedule))
            {
                var run = Assert.IsType<Guid>(await Scalar("SELECT id FROM runs WHERE series_id=$1 AND trigger=2", schedule.Series));
                Assert.True(third.Recovery.RecoveredRuns.ContainsKey(run));
            }
        }
        Assert.Empty(server.Failures);
    }
}
