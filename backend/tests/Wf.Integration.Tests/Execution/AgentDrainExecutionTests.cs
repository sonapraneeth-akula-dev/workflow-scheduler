using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core.Placement;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed partial class HostedRunExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentDrain_deadline_preserves_capacity_until_exact_physical_ack_then_replaces_without_author_failure(bool reserved)
    {
        var pool = "drain-" + Guid.NewGuid().ToString("N");
        await using var runtime = await Runtime.Create(database, w => w.Node("work", n =>
        {
            if (reserved) n.RunsOn(Machines.All(Machines.Pool(pool), Machines.Count(1)));
            n.Task("task", _ => { });
        }), teamName: pool);
        await runtime.Tick();
        var original = Assert.Single(await runtime.Pending());
        var spare = await runtime.AddAgent();
        await runtime.Execute("UPDATE agents SET supports_drain_commands=true WHERE id=$1", runtime.Session.AgentId);
        using var registry = new AgentRegistry(database.DataSource, runtime.Clock);
        await registry.LoadAsync(Token);
        var drains = new AgentDrainService(database.DataSource, runtime.Store, registry, runtime.Clock);
        var record = await drains.RequestAsync(runtime.Session.AgentId, 1, "maintenance", Guid.NewGuid().ToString("D"), "User", Token);
        Assert.Empty(await runtime.Pending());
        runtime.Clock.Now = runtime.Clock.Now.AddSeconds(2);
        await runtime.Tick();
        Assert.Empty(await runtime.Pending(spare));
        Assert.Equal((short)ExecutionStatus.Dispatched,
            await runtime.Scalar("SELECT status FROM task_attempts WHERE run_id=$1"));
        await drains.AcceptAsync(runtime.Session,
            new() { Id = record.Id.ToString("D"), ReservationEpoch = original.ReservationEpoch + 1 }, Token);
        Assert.Null((await drains.FindAsync(runtime.Session.AgentId, Token))!.ReleasedAt);
        if (reserved)
            Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL"));
        await drains.AcceptAsync(runtime.Session,
            new() { Id = record.Id.ToString("D"), ReservationEpoch = original.ReservationEpoch }, Token);
        Assert.Equal("Released", (await drains.FindAsync(runtime.Session.AgentId, Token))!.Status);
        Assert.Equal(0L, await runtime.Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL"));
        using var rebuilt = new Dispatcher(database.DataSource, runtime.Store, registry, new PlacementEngine(), runtime.Clock);
        Assert.Equal(AgentLossDisposition.Replaced,
            await rebuilt.ReplaceLostAsync(runtime.RunId, "work/task", 1, Token));
        var replacement = Assert.Single(await runtime.Pending(spare)).Command;
        Assert.Equal(2u, replacement.Task.Attempt);
        Assert.Equal((short)0, await runtime.Scalar("SELECT author_retries FROM task_attempts WHERE run_id=$1 AND attempt=2"));
        Assert.Equal((short)1, await runtime.Scalar("SELECT platform_retries FROM task_attempts WHERE run_id=$1 AND attempt=2"));
        await runtime.Complete(replacement, session: spare);
        await runtime.Tick();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Succeeded, snapshot.Status);
        Assert.Equal(ExecutionStatus.Succeeded, snapshot.Tasks["work/task"]);
    }

    [Fact]
    public async Task AgentDrain_preserves_successful_attempt_before_releasing_its_reserved_machine()
    {
        var pool = "drain-success-" + Guid.NewGuid().ToString("N");
        await using var runtime = await Runtime.Create(database, w =>
            w.Node("work", n => n.RunsOn(Machines.All(Machines.Pool(pool), Machines.Count(1))).Task("task", _ => { })),
            teamName: pool);
        await runtime.Tick();
        var command = Assert.Single(await runtime.Pending());
        await runtime.Execute("UPDATE agents SET supports_drain_commands=true WHERE id=$1", runtime.Session.AgentId);
        using var registry = new AgentRegistry(database.DataSource, runtime.Clock);
        await registry.LoadAsync(Token);
        var drains = new AgentDrainService(database.DataSource, runtime.Store, registry, runtime.Clock);
        var drain = await drains.RequestAsync(runtime.Session.AgentId, 60, "maintenance", Guid.NewGuid().ToString("D"), "User", Token);
        await runtime.Complete(command);
        Assert.Null((await drains.FindAsync(runtime.Session.AgentId, Token))!.ReleasedAt);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL"));
        await drains.AcceptAsync(runtime.Session,
            new() { Id = drain.Id.ToString("D"), ReservationEpoch = command.ReservationEpoch }, Token);
        await runtime.Tick();
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        Assert.Equal((short)ExecutionStatus.Succeeded,
            await runtime.Scalar("SELECT status FROM task_attempts WHERE run_id=$1"));
    }
}
