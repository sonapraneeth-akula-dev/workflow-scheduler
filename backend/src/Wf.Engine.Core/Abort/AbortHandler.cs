using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Stages;
using Wf.Engine.Core.StateMachine;
using Wf.Engine.Core.Transitions;

namespace Wf.Engine.Core.Abort;

public sealed record AbortPlan(ImmutableArray<NodeStateChange> Nodes, ImmutableArray<TaskStateChange> Tasks,
    ImmutableArray<string> CancelRunning, RunStatus? Status = null, AbortRecord? Record = null);

public static class AbortHandler
{
    public static AbortPlan Request(RunSnapshot snapshot, AbortRecord record)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(record);
        if (snapshot.Abort is not null && snapshot.Status is RunStatus.Aborting or RunStatus.Aborted)
            return new([], [], []);
        if (TransitionTable.IsTerminal(snapshot.Status))
            throw new InvalidOperationException($"Run '{snapshot.RunId}' is terminal ({snapshot.Status}); abort is rejected.");
        if (snapshot.Status == RunStatus.Aborting)
            throw new InvalidOperationException("An aborting run must contain its original abort record.");
        if (snapshot.Status == RunStatus.Running)
            return new([], [], [], RunStatus.Aborting, record);
        return new(snapshot.Nodes.Where(p => !TransitionTable.IsTerminal(p.Value))
                .Select(p => new NodeStateChange(p.Key, ExecutionStatus.Skipped)).ToImmutableArray(),
            snapshot.Tasks.Where(p => !TransitionTable.IsTerminal(p.Value))
                .Select(p => new TaskStateChange(p.Key, ExecutionStatus.Skipped)).ToImmutableArray(),
            [], RunStatus.Aborted, record);
    }

    public static AbortPlan Plan(ImmutableArray<NodeDefinition> nodes, RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (nodes.IsDefault)
            throw new ArgumentException("Nodes must be initialized.", nameof(nodes));
        if (snapshot.Status != RunStatus.Aborting || snapshot.Abort is null)
            throw new InvalidOperationException("Abort planning requires an aborting run and its original record.");
        var nodeChanges = ImmutableArray.CreateBuilder<NodeStateChange>();
        var taskChanges = ImmutableArray.CreateBuilder<TaskStateChange>();
        var cancel = ImmutableArray.CreateBuilder<string>();
        var targets = snapshot.AbortTargets.IsDefault ? [] : snapshot.AbortTargets.ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var nodeState = snapshot.Nodes[node.Key];
            if (TransitionTable.IsTerminal(nodeState))
                continue;
            var interrupted = node.Tasks.Any(t => targets.Contains($"{node.Key}/{t.Key}"));
            var cleanup = LifecycleStages.After.Contains(node.Key) ||
                node.Dependencies.Any(d => d.When == DependencyCondition.Always);
            if (cleanup && !interrupted)
                continue;
            var active = false;
            foreach (var task in node.Tasks)
            {
                var key = $"{node.Key}/{task.Key}";
                var state = snapshot.Tasks[key];
                if (state is ExecutionStatus.Dispatched or ExecutionStatus.Running)
                {
                    cancel.Add(key);
                    active = true;
                }
                else if (!TransitionTable.IsTerminal(state))
                    taskChanges.Add(new(key, ExecutionStatus.Skipped));
            }
            if (!active)
                nodeChanges.Add(new(node.Key, nodeState is ExecutionStatus.Pending or ExecutionStatus.Ready or ExecutionStatus.Retrying
                    ? ExecutionStatus.Skipped : ExecutionStatus.Cancelled));
        }
        var allNodesTerminal = snapshot.Nodes.All(p => TransitionTable.IsTerminal(p.Value) ||
            nodeChanges.Any(c => c.Node == p.Key));
        var allTasksTerminal = snapshot.Tasks.All(p => TransitionTable.IsTerminal(p.Value) ||
            taskChanges.Any(c => c.Task == p.Key));
        return new(nodeChanges.ToImmutable(), taskChanges.ToImmutable(), cancel.ToImmutable(),
            allNodesTerminal && allTasksTerminal ? RunStatus.Aborted : null);
    }
}
