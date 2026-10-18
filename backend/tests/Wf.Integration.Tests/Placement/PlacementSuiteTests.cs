using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core.StateMachine;
using Wf.Integration.Tests.Execution;
using Wf.Sdk;
using Runtime = Wf.Integration.Tests.Execution.HostedRunExecutionTests.Runtime;

namespace Wf.Integration.Tests.Placement;

public sealed class PlacementSuiteTests(HostedExecutionDatabase database) : IClassFixture<HostedExecutionDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("os")]
    [InlineData("pool")]
    [InlineData("label")]
    [InlineData("name")]
    [InlineData("id")]
    [InlineData("combined")]
    public async Task Three_agent_selectors_dispatch_only_to_the_matching_worker(string kind)
    {
        var ids = FleetIds();
        var unique = Guid.NewGuid().ToString("N");
        var selector = kind switch
        {
            "os" => Os.Linux,
            "pool" => Machines.Pool(unique),
            "label" => Machines.Label("sdk", "10"),
            "name" => Machines.Named(unique),
            "id" => Machines.Ids(ids[1]),
            _ => Machines.All(Os.Linux, Machines.Pool(unique), Machines.Label("sdk", "10"), Machines.Named(unique))
        };
        if (kind is not ("id" or "name" or "combined"))
            selector = Machines.All(selector, Machines.Ids(ids), Machines.Count(1));
        await using var runtime = await Runtime.Create(database, w => w.Node("work", n => n
            .Task("task", _ => { }, p => p.RunsOn(selector))), agentId: ids[0]);
        var match = await runtime.AddAgent(id: ids[1], os: "linux", pool: unique, name: unique, labels: new() { ["sdk"] = "10" });
        var other = await runtime.AddAgent(id: ids[2], labels: new() { ["sdk"] = "9" });
        await runtime.FreshTick();
        Assert.Empty(await runtime.Pending(runtime.Session));
        Assert.Empty(await runtime.Pending(other));
        var dispatch = Assert.Single(await runtime.Pending(match)).Command;
        await runtime.Complete(dispatch, session: match);
        await Finish(runtime, [runtime.Session, match, other]);
        Assert.Equal(ids[1], await runtime.Scalar("SELECT agent_id FROM task_attempts WHERE run_id=$1"));
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
    }

    [Fact]
    public async Task Three_agent_capacity_uses_each_free_slot_without_overbooking_or_phantom_attempts()
    {
        var ids = FleetIds();
        await using var runtime = await Runtime.Create(database, w => w.RunsOn(Machines.Ids(ids))
            .Node("work", n => n.Parallel(4).Task("a", _ => { }).Task("b", _ => { })
                .Task("c", _ => { }).Task("d", _ => { })), maxTasks: 1, agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1], maxTasks: 1),
            await runtime.AddAgent(id: ids[2], maxTasks: 1) };
        await runtime.FreshTick();
        foreach (var session in fleet) Assert.Single(await runtime.Pending(session));
        Assert.Equal(3L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        Assert.Equal(3L, await runtime.Scalar("SELECT count(*) FROM dispatch_outbox WHERE run_id=$1"));
        Assert.Single((await runtime.Load()).Snapshot.TaskWaitReasons);
        await runtime.FreshTick();
        Assert.Equal(3L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        await Finish(runtime, fleet);
        Assert.Equal(4L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        Assert.Equal(3L, await runtime.Scalar("SELECT count(DISTINCT agent_id) FROM task_attempts WHERE run_id=$1"));
    }

    [Theory]
    [InlineData(AffinityKind.None)]
    [InlineData(AffinityKind.Node)]
    [InlineData(AffinityKind.Run)]
    [InlineData(AffinityKind.SameMachineAs)]
    public async Task Three_agent_affinity_survives_dispatcher_reconstruction_between_every_task(AffinityKind kind)
    {
        var ids = FleetIds();
        var affinity = kind switch
        {
            AffinityKind.Node => Affinity.Node,
            AffinityKind.Run => Affinity.Run,
            AffinityKind.SameMachineAs => Affinity.SameMachineAs("first"),
            _ => Affinity.None
        };
        await using var runtime = await Runtime.Create(database, w => w.RunsOn(Machines.Ids(ids))
            .Node("first", n => n.Affinity(kind == AffinityKind.Run ? Affinity.Run :
                kind == AffinityKind.Node ? Affinity.Node : Affinity.None).Task("a", _ => { }).Task("b", _ => { }))
            .Node("second", n => n.DependsOn("first").Affinity(affinity).Task("c", _ => { }).Task("d", _ => { })),
            agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1]), await runtime.AddAgent(id: ids[2]) };
        await Finish(runtime, fleet);
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
        if (kind == AffinityKind.Node)
        {
            Assert.Equal(1L, await runtime.Scalar("""
                SELECT count(DISTINCT agent_id) FROM task_attempts WHERE run_id=$1 AND task_key LIKE 'first/%'
                """));
            Assert.Equal(1L, await runtime.Scalar("""
                SELECT count(DISTINCT agent_id) FROM task_attempts WHERE run_id=$1 AND task_key LIKE 'second/%'
                """));
        }
        else if (kind == AffinityKind.SameMachineAs)
            Assert.Equal(true, await runtime.Scalar("""
                SELECT bool_and(agent_id=(SELECT agent_id FROM task_attempts
                    WHERE run_id=$1 AND task_key='first/a'))
                FROM task_attempts WHERE run_id=$1 AND task_key LIKE 'second/%'
                """));
        else if (kind == AffinityKind.Run)
            Assert.Equal(1L, await runtime.Scalar("SELECT count(DISTINCT agent_id) FROM task_attempts WHERE run_id=$1"));
        else
            Assert.Equal(3L, await runtime.Scalar("SELECT count(DISTINCT agent_id) FROM task_attempts WHERE run_id=$1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_agent_placement_wait_expires_at_exact_ten_minutes_without_resetting_on_restart(bool soft)
    {
        var ids = FleetIds();
        await using var runtime = await Runtime.Create(database, w => w.RunsOn(Machines.Ids(ids))
            .Node("work", n => n.Task("task", _ => { }, failure: soft ? Failure.Soft : Failure.Hard)),
            agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1]), await runtime.AddAgent(id: ids[2]) };
        foreach (var session in fleet) await runtime.Execute("UPDATE agents SET status=2 WHERE id=$1", session.AgentId);
        await runtime.FreshTick();
        var waiting = (await runtime.Load()).Snapshot;
        Assert.Equal("WaitingForMachine", waiting.TaskWaitReasons["work/task"]);
        var queued = waiting.Metrics!.Tasks["work/task"].QueuedAt!.Value;
        runtime.Clock.Now = queued.AddMinutes(10).AddTicks(-10);
        await runtime.FreshTick();
        Assert.Equal(ExecutionStatus.Ready, (await runtime.Load()).Snapshot.Tasks["work/task"]);
        Assert.Equal(queued, (await runtime.Load()).Snapshot.Metrics!.Tasks["work/task"].QueuedAt);
        runtime.Clock.Now = queued.AddMinutes(10);
        await runtime.FreshTick();
        var failed = (await runtime.Load()).Snapshot;
        Assert.Equal(soft ? ExecutionStatus.SoftFailed : ExecutionStatus.Failed, failed.Tasks["work/task"]);
        Assert.Equal(FailureCategory.Placement, failed.Metrics!.Tasks["work/task"].FailureCategory);
        Assert.Equal(soft ? RunStatus.Succeeded : RunStatus.Failed, failed.Status);
        Assert.Equal(0L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("node")]
    [InlineData("run")]
    [InlineData("same")]
    [InlineData("override")]
    public async Task Three_agent_loss_replaces_onto_a_live_worker_and_rejects_old_session_completion(string mode)
    {
        var ids = FleetIds();
        var names = ids.Select(id => "loss-" + id.ToString("N")).ToArray();
        await using var runtime = await Runtime.Create(database, w =>
        {
            w.RunsOn(Machines.Named(names.Select(name => (SelectorValue)name).ToArray()));
            if (mode == "same") w.Node("reference", n => n.Task("anchor", _ => { }));
            w.Node("work", n =>
            {
                if (mode == "same") n.DependsOn("reference");
                n.Affinity(mode switch
                {
                    "node" => Affinity.Node, "run" => Affinity.Run,
                    "same" => Affinity.SameMachineAs("reference"), _ => Affinity.None
                }).Task("task", _ => { }).Task("next", _ => { });
            });
        }, mode == "override" ? new(true) : null, agentName: names[0], agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1], name: names[1]),
            await runtime.AddAgent(id: ids[2], name: names[2]) };
        await runtime.FreshTick();
        if (mode == "same")
        {
            var anchor = Assert.Single(await Active(runtime, fleet));
            await runtime.Complete(anchor.Command, session: anchor.Session);
            await runtime.FreshTick();
        }
        var lost = Assert.Single(await Active(runtime, fleet));
        await runtime.LoseAndReplace(lost.Session);
        var replacement = Assert.Single(await Active(runtime, fleet.Where(session => session != lost.Session)));
        Assert.NotEqual(lost.Session.AgentId, replacement.Session.AgentId);
        Assert.Equal(2u, replacement.Command.Task.Attempt);
        var before = (await runtime.Load()).Snapshot.LastSequence;
        await runtime.Complete(lost.Command, session: lost.Session);
        Assert.Equal(before, (await runtime.Load()).Snapshot.LastSequence);
        Assert.Equal(ExecutionStatus.Dispatched, (await runtime.Load()).Snapshot.Tasks["work/task"]);
        if (mode == "override")
            Assert.Equal(replacement.Session.AgentId, (await runtime.Load()).Snapshot.PlacementAgentId);
        await Finish(runtime, fleet);
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
        Assert.Equal(mode == "same" ? 4L : 3L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        Assert.Equal(1L, await runtime.Scalar("""
            SELECT count(*) FROM task_attempts WHERE run_id=$1 AND attempt=1 AND status=7 AND failure_category=2
            """));
        if (mode != "none")
            Assert.Equal(replacement.Session.AgentId, await runtime.Scalar(
                "SELECT agent_id FROM task_attempts WHERE run_id=$1 AND task_key='work/next'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_agent_single_machine_override_never_spreads_across_idle_workers(bool named)
    {
        var ids = FleetIds();
        var name = "suite-" + Guid.NewGuid().ToString("N");
        await using var runtime = await Runtime.Create(database, w => w.RunsOn(Machines.All(Machines.Ids(ids), Machines.Count(1)))
            .Node("work", n => n.Parallel(3).Task("a", _ => { }).Task("b", _ => { }).Task("c", _ => { })),
            new(true, named ? name : null), maxTasks: 1, agentName: name, agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1], maxTasks: 1),
            await runtime.AddAgent(id: ids[2], maxTasks: 1) };
        await runtime.FreshTick();
        Assert.Single(await Active(runtime, fleet));
        var pinned = (await runtime.Load()).Snapshot.PlacementAgentId;
        if (named) Assert.Equal(ids[0], pinned);
        await Finish(runtime, fleet);
        Assert.Equal(pinned, (await runtime.Load()).Snapshot.PlacementAgentId);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(DISTINCT agent_id) FROM task_attempts WHERE run_id=$1"));
    }

    [Fact]
    public async Task Three_agent_reservation_excludes_other_runs()
    {
        var ids = FleetIds();
        await using var runtime = await Runtime.Create(database, w => w.RunsOn(Machines.All(Machines.Ids(ids), Machines.Count(1)))
            .Node("work", n => n.Task("task", _ => { })), agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1]), await runtime.AddAgent(id: ids[2]) };
        await using var foreign = await Runtime.Create(database, w => w.Node("owner", n => n.Task("hold", _ => { })));
        var manager = new ReservationManager(database.DataSource, foreign.Store, runtime.Clock);
        var reservations = await manager.ReserveAsync(foreign.RunId, "owner", 2, new(machineIds: [ids[0], ids[1]]),
            TimeSpan.FromMinutes(5), Token);
        Assert.Equal(2, reservations.Count);
        await runtime.FreshTick();
        var active = Assert.Single(await Active(runtime, fleet));
        Assert.Equal(ids[2], active.Session.AgentId);
        Assert.Equal(2L, await foreign.Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL"));
        await Finish(runtime, fleet);
        Assert.Equal(2L, await foreign.Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL"));
    }

    [Fact]
    public async Task Three_agent_reservation_limits_the_owner_to_its_reserved_cohort()
    {
        var ids = FleetIds();
        await using var runtime = await Runtime.Create(database, w => w.RunsOn(Machines.All(Machines.Ids(ids), Machines.Count(2)))
            .Node("work", n => n.Parallel(2).Task("a", _ => { }).Task("b", _ => { })), agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1]), await runtime.AddAgent(id: ids[2]) };
        var manager = new ReservationManager(database.DataSource, runtime.Store, runtime.Clock);
        var reservations = await manager.ReserveAsync(runtime.RunId, "work", 2,
            new(machineIds: [ids[0], ids[1], ids[2]], count: 2), TimeSpan.FromMinutes(5), Token);
        Assert.Equal(2, reservations.Count);
        await runtime.FreshTick();
        var active = await Active(runtime, fleet);
        Assert.Equal(2, active.Count);
        Assert.All(active, item => Assert.Contains(item.Session.AgentId, reservations.Select(reservation => reservation.AgentId)));
        Assert.Empty(await runtime.Pending(fleet.Single(session => reservations.All(reservation => reservation.AgentId != session.AgentId))));
        foreach (var (session, command) in active) await runtime.Complete(command, session: session);
        await runtime.FreshTick();
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_agent_loss_does_not_bypass_named_override_or_NoAutoRetry(bool named)
    {
        var ids = FleetIds();
        var name = "no-move-" + Guid.NewGuid().ToString("N");
        await using var runtime = await Runtime.Create(database, w => w.Node("work", n =>
        {
            if (!named) n.NoAutoRetry();
            n.Task("task", _ => { });
        }), named ? new(true, name) : null, agentName: name, agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1]), await runtime.AddAgent(id: ids[2]) };
        await runtime.FreshTick();
        var lost = Assert.Single(await Active(runtime, fleet));
        await runtime.LoseAndReplace(lost.Session);
        await runtime.FreshTick();
        var failed = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Equal(named ? FailureCategory.Placement : FailureCategory.AgentLost,
            failed.Metrics!.Tasks["work/task"].FailureCategory);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        Assert.Empty(await Active(runtime, fleet));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("attempt")]
    [InlineData("run")]
    [InlineData("event")]
    [InlineData("missing")]
    public async Task Three_agent_recovery_rejects_corrupt_or_incomplete_placement_history_without_new_dispatch(string corruption)
    {
        var ids = FleetIds();
        await using var runtime = await Runtime.Create(database, w => w.RunsOn(Machines.Ids(ids))
            .Affinity(Affinity.Run).Node("work", n => n.Task("first", _ => { }).Task("second", _ => { })),
            agentId: ids[0]);
        var fleet = new[] { runtime.Session, await runtime.AddAgent(id: ids[1]), await runtime.AddAgent(id: ids[2]) };
        await runtime.FreshTick();
        var first = Assert.Single(await Active(runtime, fleet));
        await runtime.Complete(first.Command, session: first.Session);
        await runtime.Execute(corruption switch
        {
            "key" => """UPDATE task_attempts SET placement_request=jsonb_set(placement_request,'{TaskKey}','"wrong"') WHERE run_id=$1""",
            "attempt" => """UPDATE task_attempts SET placement_request=jsonb_set(placement_request,'{Attempt}','2') WHERE run_id=$1""",
            "run" => """UPDATE task_attempts SET placement_request=jsonb_set(placement_request,'{RunId}',to_jsonb(gen_random_uuid()::text)) WHERE run_id=$1""",
            "event" => "DELETE FROM run_events WHERE run_id=$1 AND kind=2 AND attempt=1",
            _ => "UPDATE task_attempts SET dispatch_payload=NULL,placement_request=NULL,execution_policy=NULL WHERE run_id=$1"
        }, runtime.RunId);
        var before = (await runtime.Load()).Snapshot.LastSequence;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(runtime.FreshTick);
            Assert.Equal(before, (await runtime.Load()).Snapshot.LastSequence);
            Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
            Assert.Empty(await Active(runtime, fleet));
        }
        finally
        {
            await runtime.Execute("UPDATE runs SET status=$2 WHERE id=$1", runtime.RunId, (short)RunStatus.Failed);
        }
    }

    private static Guid[] FleetIds() => [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private static async Task<List<(AgentSession Session, Dispatch Command)>> Active(
        Runtime runtime, IEnumerable<AgentSession> fleet)
    {
        var active = new List<(AgentSession, Dispatch)>();
        foreach (var session in fleet)
            active.AddRange((await runtime.Pending(session)).Select(pending => (session, pending.Command)));
        return active;
    }

    private static async Task Finish(Runtime runtime, AgentSession[] fleet)
    {
        for (var step = 0; step < 32; step++)
        {
            await runtime.FreshTick();
            var snapshot = (await runtime.Load()).Snapshot;
            if (TransitionTable.IsTerminal(snapshot.Status))
            {
                Assert.Equal(RunStatus.Succeeded, snapshot.Status);
                return;
            }
            var active = await Active(runtime, fleet);
            Assert.NotEmpty(active);
            foreach (var (session, command) in active) await runtime.Complete(command, session: session);
        }
        Assert.Fail("The three-agent placement run did not terminate after 32 planner ticks.");
    }
}
