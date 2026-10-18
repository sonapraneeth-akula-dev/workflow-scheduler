using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;
using Wf.Sdk;
using Clock = Wf.Integration.Tests.Registration.RegistrationTests.Clock;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests;

public sealed class SelectorPlacementTests(DispatchDatabase database) : IClassFixture<DispatchDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(true, "windows")]
    [InlineData(false, "windows")]
    [InlineData(true, "linux")]
    [InlineData(false, "linux")]
    public async Task SDK_selector_dispatches_only_to_matching_mixed_OS_agents_and_preserves_busy_cohort_without_spillover(
        bool explicitIds, string os)
    {
        var clock = new Clock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry);
        var expected = os == "windows" ? fleet[..2] : fleet[2..];
        var selector = explicitIds ? Machines.Ids(expected.Select(session => session.AgentId).ToArray()) :
            Machines.All(Machines.Pool(fleet[0].AgentId.ToString("N")), Machines.Label("role", "perf"),
                os == "windows" ? Os.Windows : Os.Linux, Machines.Count(expected.Length));
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var (run, policy) = await Ready(store, selector, clock);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        var dispatched = new List<Guid>();
        foreach (var task in new[] { "first", "second", "third" }.Take(expected.Length))
        {
            var decision = await Send(dispatcher, run, task, policy, clock);
            Assert.NotNull(decision.Agent);
            Assert.Contains(decision.Agent.Id, expected.Select(session => session.AgentId));
            Assert.Equal(os, decision.Agent.Os);
            dispatched.Add(decision.Agent.Id);
        }
        Assert.Equal(expected.Length, dispatched.Distinct().Count());
        var waitingTask = expected.Length == 1 ? "second" : "third";
        var sequence = run.Snapshot.LastSequence;
        Assert.Null((await Send(dispatcher, run, waitingTask, policy, clock)).Agent);
        Assert.Equal(sequence, run.Snapshot.LastSequence);
        Assert.Equal((long)expected.Length, await Count("task_attempts", run.Snapshot.RunId));
        Assert.Equal((long)expected.Length, await Count("dispatch_outbox", run.Snapshot.RunId));
        var restoredRun = await store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(ExecutionStatus.Ready, restoredRun.Snapshot.Tasks["work/" + waitingTask]);
        using (var restartedRegistry = new AgentRegistry(database.DataSource, clock))
        {
            await restartedRegistry.LoadAsync(Token);
            using var restarted = new Dispatcher(database.DataSource, store, restartedRegistry, new PlacementEngine(), clock);
            Assert.Null((await Send(restarted, restoredRun, waitingTask, policy, clock)).Agent);
        }
        var selected = expected.Single(session => session.AgentId == dispatched[0]);
        var pending = Assert.Single(await store.PendingAsync(selected, Token));
        Assert.Equal("first", pending.Command.Task.TaskKey);
        Assert.Equal(1UL, pending.Command.ReservationEpoch);
        await store.AcceptAsync(selected, new AgentMessage { TaskStarted = new()
        {
            Task = pending.Command.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.Now)
        } }, Token);
        await store.AcceptAsync(selected, new AgentMessage { TaskCompleted = new()
        {
            Task = pending.Command.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.Now),
            Outcome = TaskOutcome.Succeeded
        } }, Token);
        restoredRun = await store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(selected.AgentId, (await Send(dispatcher, restoredRun, waitingTask, policy, clock)).Agent!.Id);
        foreach (var foreign in fleet.Except(expected))
            Assert.Empty(await store.PendingAsync(foreign, Token));
    }

    [Fact]
    public async Task Count_above_mixed_fleet_matches_waits_before_attempt_outbox_event_or_round_robin_mutation()
    {
        var clock = new Clock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry);
        var selector = Machines.All(Os.Linux, Machines.Pool(fleet[0].AgentId.ToString("N")), Machines.Count(2));
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var (run, policy) = await Ready(store, selector, clock);
        var engine = new PlacementEngine();
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, engine, clock);
        var before = run.Snapshot.LastSequence;
        var decision = await Send(dispatcher, run, "first", policy, clock);
        Assert.Null(decision.Agent);
        Assert.Equal(PlacementReason.WaitingForMachine, decision.Reason);
        Assert.Equal(PlacementSnapshot.Empty, engine.Snapshot);
        Assert.Equal(before, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Equal(0L, await Count("task_attempts", run.Snapshot.RunId));
        Assert.Equal(0L, await Count("dispatch_outbox", run.Snapshot.RunId));
    }

    [Fact]
    public async Task Count_one_never_spills_to_a_second_matching_windows_machine_while_its_selected_member_is_busy()
    {
        var clock = new Clock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var fleet = await Fleet(registry);
        var selector = Machines.All(Os.Windows, Machines.Pool(fleet[0].AgentId.ToString("N")), Machines.Count(1));
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var (run, policy) = await Ready(store, selector, clock);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        var first = await Send(dispatcher, run, "first", policy, clock);
        Assert.Equal(fleet[..2].Min(session => session.AgentId), first.Agent!.Id);
        Assert.Null((await Send(dispatcher, run, "second", policy, clock)).Agent);
        var other = fleet[..2].Single(session => session.AgentId != first.Agent.Id);
        Assert.Empty(await store.PendingAsync(other, Token));
        Assert.Equal(1L, await Count("task_attempts", run.Snapshot.RunId));
    }

    private static async Task<AgentSession[]> Fleet(AgentRegistry registry)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var sessions = new List<AgentSession>();
        for (var index = 0; index < ids.Length; index++)
        {
            var hello = new Hello
            {
                Id = ids[index].ToString("D"), Name = "selector-" + ids[index].ToString("N"),
                Os = index == 2 ? "linux" : "windows", Arch = "x64", Cores = 4,
                RamMb = 8192, MaxTasks = 1, Pool = ids[0].ToString("N"), Version = "1.0", ProtocolVersion = 1
            };
            hello.Labels.Add("role", "perf");
            sessions.Add(await registry.RegisterAsync(hello, Token));
        }
        return sessions.ToArray();
    }

    private static async Task<(RunState Run, PlacementPolicy Policy)> Ready(RunStore store, PlacementSelector selector, Clock clock)
    {
        var workflow = Workflow.Define("tests/perf/selector", "1.0.0", builder => builder.Type("perf")
            .Node("work", node => node.Parallel(3).RunsOn(selector)
                .Task("first", _ => { }).Task("second", _ => { }).Task("third", _ => { })));
        var manifest = JsonSerializer.Deserialize(ManifestWriter.WriteUtf8(workflow),
            WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var run = new RunState(Guid.CreateVersion7(clock.Now), manifest.Nodes, manifest.Metadata, Assert.Fail);
        await store.CreateAsync(run, manifest.Name, Guid.NewGuid(), clock.Now.AddMinutes(5), Token);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, clock.Now, Token);
        foreach (var task in manifest.Nodes[0].Tasks)
            await store.AppendAsync(run, EventEntity.Task, "work/" + task.Key, (int)ExecutionStatus.Ready, clock.Now, Token);
        return (run, manifest.Nodes[0].Placement!);
    }

    private static Task<PlacementDecision> Send(Dispatcher dispatcher, RunState run, string task, PlacementPolicy policy, Clock clock) =>
        dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, "work", task, 1, Sha, policy), new DispatchCommand
        {
            Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = task, Attempt = 1 },
            Version = "1.0.0", PackageUri = "https://packages.invalid/selector.zip", Sha256 = Sha,
            DeadlineAt = Timestamp.FromDateTimeOffset(clock.Now.AddMinutes(1))
        }, Token);

    private async Task<long> Count(string table, Guid run)
    {
        await using var command = database.DataSource.CreateCommand($"SELECT count(*) FROM {table} WHERE run_id=$1");
        command.Parameters.AddWithValue(run);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
}
