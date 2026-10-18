using System.Collections.Immutable;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core.Abort;
using Wf.Engine.Core.Scheduling;
using Wf.Engine.Core.Stages;
using Wf.Engine.Core.StateMachine;
using Wf.Engine.Core.Transitions;

namespace Wf.Engine.Core.Tests;

public sealed class StateMachinePropertyTests
{
    private enum AbortPoint { None, Queued, Running, Backoff, PostProcess, Twice }
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GeneratedDagsConserveEveryTaskAndReachExactlyOneTerminalState()
    {
        Check.QuickThrowOnFailure(Prop.ForAll<int, int>((seed, mode) =>
        {
            Execute(seed, (int)((uint)seed % 196) + 1, (AbortPoint)((uint)mode % 6));
            return true;
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TwoHundredNodeBoundaryCoversEveryAbortPoint(int mode) => Execute(20261015, 196, (AbortPoint)mode);

    private static void Execute(int seed, int bodyCount, AbortPoint point)
    {
        var random = new Random(seed);
        var nodes = Generate(random, bodyCount);
        var run = new RunState(Guid.CreateVersion7(), nodes,
            new("property", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        var initial = run.Snapshot;
        var checkpoint = initial;
        var abort = new AbortRecord("property", "Property test", Now, $"seed={seed}; point={point}", AbortSource.Local);
        var aborted = false;
        var duplicate = false;
        var retried = false;
        if (point == AbortPoint.Queued)
            RequestAbort();
        else
            Emit(EventEntity.Run, null, (int)RunStatus.Running);

        // This driver supplies randomized host acknowledgements, not a second scheduler.
        for (var step = 0; step < nodes.Length * 40 + 200; step++)
        {
            if (TransitionTable.IsTerminal(run.Snapshot.Status))
                break;
            if (step == nodes.Length)
                checkpoint = run.Snapshot;
            var sequenceBeforePlanning = run.Snapshot.LastSequence;
            if (run.Snapshot.Status == RunStatus.Aborting)
                Apply(AbortHandler.Plan(nodes, run.Snapshot));
            if (TransitionTable.IsTerminal(run.Snapshot.Status))
                break;
            var stages = StagePlanner.Plan(nodes, run.Snapshot.Nodes, run.Snapshot.Status);
            foreach (var change in stages.Changes)
                ChangeNode(change);
            foreach (var change in ReadinessEvaluator.Evaluate(nodes, run.Snapshot.Nodes, stages.EligibleNodes))
                ChangeNode(change);
            if (stages.Complete)
            {
                if (run.Snapshot.Status == RunStatus.Aborting)
                    Apply(AbortHandler.Plan(nodes, run.Snapshot));
                else
                    Emit(EventEntity.Run, null, (int)(run.Snapshot.Nodes.Values.Any(s =>
                        s is ExecutionStatus.Failed or ExecutionStatus.TimedOut) ? RunStatus.Failed : RunStatus.Succeeded));
                continue;
            }

            var actions = new List<Action>();
            foreach (var node in nodes)
            {
                var status = run.Snapshot.Nodes[node.Key];
                if (status == ExecutionStatus.Ready)
                {
                    actions.Add(() =>
                    {
                        Assert.All(node.Dependencies, dependency =>
                            Assert.True(ReadinessEvaluator.IsSatisfied(dependency.When, run.Snapshot.Nodes[dependency.Node])));
                        Emit(EventEntity.Node, node.Key, (int)ExecutionStatus.Dispatched);
                        Emit(EventEntity.Node, node.Key, (int)ExecutionStatus.Running);
                    });
                    continue;
                }
                if (status != ExecutionStatus.Running)
                    continue;
                var local = node.Tasks.ToDictionary(t => t.Key, t => run.Snapshot.Tasks[$"{node.Key}/{t.Key}"]);
                var outcome = FailurePropagation.ResolveNode(node, local);
                if (outcome is { } terminal)
                {
                    actions.Add(() => Emit(EventEntity.Node, node.Key, (int)terminal));
                    continue;
                }
                var active = local.Values.Count(s => s is ExecutionStatus.Running or ExecutionStatus.Dispatched);
                foreach (var task in node.Tasks)
                {
                    var key = $"{node.Key}/{task.Key}";
                    if (local[task.Key] is ExecutionStatus.Pending or ExecutionStatus.Retrying &&
                        active < (node.Mode == NodeExecutionMode.Series ? 1 : node.MaxParallel ?? int.MaxValue) &&
                        (node.Mode == NodeExecutionMode.Parallel ||
                            node.Tasks.TakeWhile(t => t.Key != task.Key).All(t => TransitionTable.IsTerminal(local[t.Key]))))
                    {
                        actions.Add(() =>
                        {
                            if (run.Snapshot.Tasks[key] == ExecutionStatus.Pending)
                                Emit(EventEntity.Task, key, (int)ExecutionStatus.Ready);
                            Emit(EventEntity.Task, key, (int)ExecutionStatus.Dispatched);
                            Emit(EventEntity.Task, key, (int)ExecutionStatus.Running);
                        });
                    }
                    else if (local[task.Key] == ExecutionStatus.Running)
                    {
                        actions.Add(() =>
                        {
                            if (!aborted && ((point is AbortPoint.Running or AbortPoint.Twice &&
                                    !LifecycleStages.IsStage(node.Key)) ||
                                (point == AbortPoint.PostProcess && node.Key == LifecycleStages.PostProcess)))
                            {
                                RequestAbort();
                                return;
                            }
                            if (!retried && point == AbortPoint.Backoff && !LifecycleStages.IsStage(node.Key))
                            {
                                retried = true;
                                Emit(EventEntity.Task, key, (int)ExecutionStatus.Retrying);
                                RequestAbort();
                                return;
                            }
                            if (point == AbortPoint.Twice && aborted && !duplicate &&
                                node.Key == LifecycleStages.PostProcess)
                            {
                                duplicate = true;
                                var second = AbortHandler.Request(run.Snapshot,
                                    new("second", "Second", Now, "Must not replace first", AbortSource.API));
                                Assert.Null(second.Status);
                                Assert.Same(abort, run.Snapshot.Abort);
                            }
                            if (random.Next(8) == 0 &&
                                (point == AbortPoint.None || !LifecycleStages.Before.Contains(node.Key)))
                            {
                                var failure = FailurePropagation.Plan(node, task.Key,
                                    node.Tasks.ToDictionary(t => t.Key, t => run.Snapshot.Tasks[$"{node.Key}/{t.Key}"]),
                                    timedOut: random.Next(2) == 0);
                                foreach (var change in failure.Changes)
                                    Emit(EventEntity.Task, $"{node.Key}/{change.Task}", (int)change.Status);
                                foreach (var sibling in failure.CancelRunning)
                                    Emit(EventEntity.Task, $"{node.Key}/{sibling}", (int)ExecutionStatus.Cancelled);
                            }
                            else
                                Emit(EventEntity.Task, key, (int)ExecutionStatus.Succeeded);
                        });
                    }
                }
            }
            if (actions.Count == 0 && run.Snapshot.LastSequence > sequenceBeforePlanning)
                continue;
            Assert.NotEmpty(actions);
            actions[random.Next(actions.Count)]();
        }

        Assert.True(TransitionTable.IsTerminal(run.Snapshot.Status), $"Run did not drain: seed={seed}; point={point}.");
        Assert.Equal(nodes.Sum(n => n.Tasks.Length), run.Snapshot.Tasks.Count);
        Assert.All(run.Snapshot.Tasks.Values, status => Assert.True(TransitionTable.IsTerminal(status)));
        Assert.All(run.Snapshot.Nodes.Values, status => Assert.True(TransitionTable.IsTerminal(status)));
        foreach (var key in run.Snapshot.Tasks.Keys)
            Assert.Single(run.Events, e => e.Entity == EventEntity.Task && e.Key == key &&
                TransitionTable.IsTerminal((ExecutionStatus)e.State));
        foreach (var key in run.Snapshot.Nodes.Keys)
            Assert.Single(run.Events, e => e.Entity == EventEntity.Node && e.Key == key &&
                TransitionTable.IsTerminal((ExecutionStatus)e.State));
        Assert.Single(run.Events, e => e.Entity == EventEntity.Run && TransitionTable.IsTerminal((RunStatus)e.State));
        Assert.Equal(point == AbortPoint.None ? 0 : 1, run.Events.Count(e => e.Abort is not null));
        if (point != AbortPoint.None)
        {
            Assert.True(aborted);
            Assert.Equal(RunStatus.Aborted, run.Snapshot.Status);
            Assert.Equal(abort, run.Snapshot.Abort);
        }
        if (point == AbortPoint.Twice)
            Assert.True(duplicate);
        var replay = RunState.Restore(initial, run.Events, Assert.Fail);
        var restored = RunState.Restore(checkpoint, run.Events.Where(e => e.Sequence > checkpoint.LastSequence), Assert.Fail);
        Assert.Equal(JsonSerializer.Serialize(run.Snapshot), JsonSerializer.Serialize(replay.Snapshot));
        Assert.Equal(JsonSerializer.Serialize(run.Snapshot), JsonSerializer.Serialize(restored.Snapshot));
        Assert.NotNull(run.Snapshot.EndedAt);

        void Emit(EventEntity entity, string? key, int state) => run.Append(entity, key, state, Now);
        void RequestAbort()
        {
            aborted = true;
            Apply(AbortHandler.Request(run.Snapshot, abort));
        }
        void Apply(AbortPlan plan)
        {
            foreach (var task in plan.Tasks)
                Emit(EventEntity.Task, task.Task, (int)task.Status);
            foreach (var node in plan.Nodes)
                ChangeNode(node);
            foreach (var key in plan.CancelRunning)
                Emit(EventEntity.Task, key, (int)ExecutionStatus.Cancelled);
            if (plan.Status is { } status)
                run.Append(EventEntity.Run, null, (int)status, Now, plan.Record);
        }
        void ChangeNode(NodeStateChange change)
        {
            if (change.Status == ExecutionStatus.Skipped)
                foreach (var task in nodes.Single(n => n.Key == change.Node).Tasks)
                {
                    var key = $"{change.Node}/{task.Key}";
                    if (!TransitionTable.IsTerminal(run.Snapshot.Tasks[key]))
                        Emit(EventEntity.Task, key, (int)ExecutionStatus.Skipped);
                }
            Emit(EventEntity.Node, change.Node, (int)change.Status);
        }
    }

    private static ImmutableArray<NodeDefinition> Generate(Random random, int bodyCount)
    {
        var nodes = ImmutableArray.CreateBuilder<NodeDefinition>();
        foreach (var key in LifecycleStages.Before)
            nodes.Add(new(key, [], [new("stage")]));
        for (var index = 0; index < bodyCount; index++)
        {
            var dependencies = Enumerable.Range(0, index).OrderBy(_ => random.Next()).Take(random.Next(4))
                .Select(parent => new DependencyDefinition($"n{parent}", (DependencyCondition)random.Next(3))).ToImmutableArray();
            var parallel = random.Next(2) == 0;
            nodes.Add(new($"n{index}", dependencies, Enumerable.Range(0, random.Next(1, 4))
                .Select(task => new TaskDefinition($"t{task}", (FailurePolicy)random.Next(2))).ToImmutableArray(),
                parallel ? NodeExecutionMode.Parallel : NodeExecutionMode.Series,
                parallel ? random.Next(1, 4) : null, (FailurePolicy)random.Next(2)));
        }
        foreach (var key in LifecycleStages.After)
            nodes.Add(new(key, [], [new("stage")]));
        return nodes.ToImmutable();
    }
}
