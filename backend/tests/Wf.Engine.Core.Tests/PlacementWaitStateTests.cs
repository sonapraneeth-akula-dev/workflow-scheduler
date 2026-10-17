using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;

namespace Wf.Engine.Core.Tests;

public sealed class PlacementWaitStateTests
{
    [Fact]
    public void Waiting_reason_replays_without_resetting_queue_time_and_dispatch_pin_is_durable()
    {
        var now = DateTimeOffset.UtcNow;
        var initial = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail, now).Snapshot with
            { Placement = new(true) };
        var run = RunState.Restore(initial, [], Assert.Fail);
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, now);
        run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, now);
        var wait = new RunEvent(initial.RunId, 3, now.AddSeconds(1), EventEntity.Task, "node/task",
            (int)ExecutionStatus.Ready) { WaitingReason = "WaitingForMachine" };
        run.Apply(wait);
        Assert.Equal("WaitingForMachine", run.Snapshot.TaskWaitReasons["node/task"]);
        Assert.Equal(now, run.Snapshot.Metrics!.Tasks["node/task"].QueuedAt);
        Assert.Equal(now, run.Snapshot.Metrics.Tasks["node/task"].WaitingSince);
        var before = run.Snapshot;
        Assert.Throws<InvalidOperationException>(() =>
            RunState.Restore(before, [wait with { Sequence = 4 }], _ => { }));
        var agent = Guid.NewGuid();
        run.Apply(run.CreateEvent(EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, now.AddSeconds(2))
            with { AgentId = agent });
        Assert.Empty(run.Snapshot.TaskWaitReasons);
        Assert.Equal(agent, run.Snapshot.PlacementAgentId);
        var reloaded = JsonSerializer.Deserialize<RunSnapshot>(JsonSerializer.Serialize(run.Snapshot))!;
        Assert.Equal(agent, RunState.Restore(reloaded, [], Assert.Fail).Snapshot.PlacementAgentId);
        Assert.Equal(JsonSerializer.Serialize(run.Snapshot),
            JsonSerializer.Serialize(RunState.Restore(initial, run.Events, Assert.Fail).Snapshot));
    }

    [Theory]
    [InlineData("wrong-reason")]
    [InlineData("wrong-state")]
    [InlineData("wrong-entity")]
    [InlineData("empty-agent")]
    public void Invalid_placement_event_metadata_is_rejected_before_mutation(string invalid)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), _ => { }, now);
        var change = new RunEvent(run.Snapshot.RunId, 1, now, EventEntity.Task, "node/task",
            (int)ExecutionStatus.Ready) { WaitingReason = "WaitingForMachine" };
        change = invalid switch
        {
            "wrong-reason" => change with { WaitingReason = "secret-input" },
            "wrong-state" => change with { State = (int)ExecutionStatus.Running },
            "wrong-entity" => change with { Entity = EventEntity.Node, Key = "node" },
            _ => change with { AgentId = Guid.Empty }
        };
        Assert.Throws<InvalidOperationException>(() => run.Apply(change));
        Assert.Equal(0, run.Snapshot.LastSequence);
    }
}
