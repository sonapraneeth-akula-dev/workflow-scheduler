using Wf.Contracts;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed partial class HostedRunExecutionTests
{
    [Fact]
    public async Task Single_machine_count_one_reservation_uses_the_combined_run_selectors_not_an_arbitrary_worker()
    {
        var name = "reservation-" + Guid.NewGuid().ToString("N");
        await using var runtime = await Runtime.Create(database, w => w.Node("work", n => n
            .RunsOn(Machines.Count(1)).Task("task", _ => { }, p => p.RunsOn(Machines.Named(name)))),
            new(true), agentName: name);
        await runtime.AddAgent(id: Guid.Parse("00000000-0000-4000-8000-000000000001"));
        await runtime.Tick();
        Assert.Single(await runtime.Pending());
        Assert.Equal(runtime.Session.AgentId, (await runtime.Load()).Snapshot.PlacementAgentId);
        Assert.Equal(runtime.Session.AgentId, await runtime.Scalar("SELECT agent_id FROM reservations WHERE run_id=$1"));
    }

    [Fact]
    public async Task Single_machine_capacity_wait_reason_and_pin_survive_reconstructed_dispatcher_without_spreading()
    {
        await using var runtime = await Runtime.Create(database, w =>
            w.Node("work", n => n.Parallel(3).Task("a", _ => { }).Task("b", _ => { }).Task("c", _ => { })),
            new(true), maxTasks: 1);
        await runtime.Tick();
        var first = Assert.Single(await runtime.Pending());
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(runtime.Session.AgentId, snapshot.PlacementAgentId);
        Assert.Equal(2, snapshot.TaskWaitReasons.Count);
        Assert.All(snapshot.TaskWaitReasons.Values, value => Assert.Equal("WaitingForMachine", value));
        var other = await runtime.AddAgent();
        await runtime.FreshTick();
        Assert.Empty(await runtime.Pending(other));
        Assert.Single(await runtime.Pending());
        await runtime.Complete(first);
        await runtime.FreshTick();
        var second = Assert.Single(await runtime.Pending());
        Assert.NotEqual(first.Task.TaskKey, second.Task.TaskKey);
        Assert.Single((await runtime.Load()).Snapshot.TaskWaitReasons);
        Assert.Empty(await runtime.Pending(other));
        await runtime.Complete(second);
        await runtime.Finish();
        Assert.Empty((await runtime.Load()).Snapshot.TaskWaitReasons);
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(DISTINCT agent_id) FROM task_attempts WHERE run_id=$1"));
    }

    [Theory]
    [InlineData(false, RunStatus.Failed, ExecutionStatus.Failed)]
    [InlineData(true, RunStatus.Succeeded, ExecutionStatus.SoftFailed)]
    public async Task WaitingForMachine_keeps_original_queue_deadline_then_fails_placement_at_exact_ten_minutes(
        bool soft, RunStatus expectedRun, ExecutionStatus expectedTask)
    {
        await using var runtime = await Runtime.Create(database, w =>
            w.Node("work", n => n.Task("task", _ => { }, failure: soft ? Failure.Soft : Failure.Hard)));
        await runtime.Execute("UPDATE agents SET status=2 WHERE id=$1", runtime.Session.AgentId);
        await runtime.Tick();
        var waiting = (await runtime.Load()).Snapshot;
        Assert.Equal("WaitingForMachine", waiting.TaskWaitReasons["work/task"]);
        var queuedAt = waiting.Metrics!.Tasks["work/task"].QueuedAt!.Value;
        runtime.Clock.Now = queuedAt.AddMinutes(10).AddTicks(-10);
        await runtime.FreshTick();
        Assert.Equal(ExecutionStatus.Ready, (await runtime.Load()).Snapshot.Tasks["work/task"]);
        Assert.Equal(queuedAt, (await runtime.Load()).Snapshot.Metrics!.Tasks["work/task"].QueuedAt);
        runtime.Clock.Now = queuedAt.AddMinutes(10);
        await runtime.FreshTick();
        var failed = (await runtime.Load()).Snapshot;
        Assert.Equal(expectedTask, failed.Tasks["work/task"]);
        Assert.Equal(expectedRun, failed.Status);
        Assert.Equal(Wf.Contracts.Definition.FailureCategory.Placement,
            failed.Metrics!.Tasks["work/task"].FailureCategory);
        Assert.Empty(failed.TaskWaitReasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_single_machine_agent_loss_fails_Placement_instead_of_moving_to_another_worker(bool soft)
    {
        var name = "named-" + Guid.NewGuid().ToString("N");
        await using var runtime = await Runtime.Create(database, w =>
            w.Node("work", n => n.Task("task", _ => { }, failure: soft ? Failure.Soft : Failure.Hard)),
            new(true, name), agentName: name);
        await runtime.Tick();
        await runtime.AddAgent();
        await runtime.LoseAndReplace(runtime.Session);
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(soft ? ExecutionStatus.SoftFailed : ExecutionStatus.Failed, snapshot.Tasks["work/task"]);
        Assert.Equal(Wf.Contracts.Definition.FailureCategory.Placement,
            snapshot.Metrics!.Tasks["work/task"].FailureCategory);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
    }

    [Fact]
    public async Task Single_machine_agent_loss_replaces_the_entire_cohort_and_moves_the_durable_pin()
    {
        await using var runtime = await Runtime.Create(database, w =>
            w.Node("work", n => n.Parallel(2).Task("a", _ => { }).Task("b", _ => { })), new(true));
        await runtime.Tick();
        Assert.Equal(2, (await runtime.Pending()).Count);
        var next = await runtime.AddAgent();
        await runtime.LoseAndReplace(runtime.Session);
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(next.AgentId, snapshot.PlacementAgentId);
        Assert.Equal(2, (await runtime.Pending(next)).Count);
        Assert.Equal(2L, await runtime.Scalar(
            "SELECT count(*) FROM task_attempts WHERE run_id=$1 AND attempt=2 AND status=2"));
        await runtime.FreshTick();
        Assert.Equal(next.AgentId, (await runtime.Load()).Snapshot.PlacementAgentId);
    }
}
