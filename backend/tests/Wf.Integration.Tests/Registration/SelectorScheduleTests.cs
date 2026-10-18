using System.Net;
using Wf.ControlPlane.Engine.Scheduler;
using Wf.Sdk;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class ScheduleFireTests
{
    [Fact]
    public async Task Selectors_scheduled_fire_rechecks_inventory_before_run_and_rolls_back_claim_and_cursor_on_failure()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var machine = await TriggerRunTests.SeedSelectorMachine(database.DataSource);
        var schedule = await CreateSchedule(server);
        var request = TriggerRunTests.WithSelector(schedule.Request with
        {
            Manifest = WithVersion(schedule.Request, "1.1.0")
        }, Machines.All(Machines.Ids(machine), Machines.Label("role", "perf")));
        using var registered = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await Execute("UPDATE agents SET labels=ARRAY['role=changed'] WHERE id=$1", machine);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var error = await Assert.ThrowsAsync<ScheduleFireRejectedException>(() => engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.Contains("WFV027", error.Message);
        Assert.Contains("work/task", error.Message);
        Assert.Contains("role", error.Message);
        Assert.DoesNotContain("changed", error.Message);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(schedule.Timer.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        await Execute("UPDATE agents SET labels=ARRAY['role=perf'],status=2,slots_used=max_tasks WHERE id=$1", machine);
        Assert.NotNull(await engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Empty(server.Failures);
    }
}
