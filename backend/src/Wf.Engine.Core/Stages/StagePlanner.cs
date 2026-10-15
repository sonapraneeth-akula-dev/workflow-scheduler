using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Stages;

public sealed record NodeStateChange(string Node, ExecutionStatus Status);
public sealed record StagePlan(ImmutableArray<string> EligibleNodes, ImmutableArray<NodeStateChange> Changes,
    bool Complete = false);

public static class StagePlanner
{
    public static StagePlan Plan(ImmutableArray<NodeDefinition> nodes,
        IReadOnlyDictionary<string, ExecutionStatus> states, RunStatus runStatus, bool timedOut = false)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (!Enum.IsDefined(runStatus))
            throw new ArgumentOutOfRangeException(nameof(runStatus));
        if (nodes.IsDefault)
            throw new ArgumentException("Nodes must be initialized.", nameof(nodes));
        foreach (var node in nodes)
            if (!states.TryGetValue(node.Key, out var state) || !Enum.IsDefined(state))
                throw new ArgumentException($"Node '{node.Key}' has no valid state.", nameof(states));
        if (TransitionTable.IsTerminal(runStatus))
            return new([], [], true);

        var keys = nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var bodyNodes = nodes.Where(n => !LifecycleStages.IsStage(n.Key)).ToArray();
        var body = bodyNodes.Select(n => n.Key).ToArray();
        var always = bodyNodes.Where(n => n.Dependencies.Any(d => d.When == DependencyCondition.Always))
            .Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var failed = states.Any(p => keys.Contains(p.Key) && LifecycleStages.Before.Contains(p.Key) &&
            p.Value is ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Cancelled);
        var stopping = failed || timedOut || runStatus == RunStatus.Aborting;
        var changes = ImmutableArray.CreateBuilder<NodeStateChange>();
        if (stopping)
        {
            foreach (var key in LifecycleStages.Before.Concat(body).Where(keys.Contains).Where(key => !always.Contains(key)))
                if (states[key] is ExecutionStatus.Pending or ExecutionStatus.Ready or ExecutionStatus.Retrying)
                    changes.Add(new(key, ExecutionStatus.Skipped));
            if (LifecycleStages.Before.Concat(body).Where(keys.Contains).Any(key =>
                    states[key] is ExecutionStatus.Dispatched or ExecutionStatus.Running))
                return new([], changes.ToImmutable());
            if (always.Any(key => !TransitionTable.IsTerminal(states[key])))
                return new(always.Where(key => !TransitionTable.IsTerminal(states[key])).ToImmutableArray(),
                    changes.ToImmutable());
        }
        else
        {
            foreach (var key in LifecycleStages.Before.Where(keys.Contains))
                if (!TransitionTable.IsTerminal(states[key]))
                    return Activate(key, states[key], changes);
            if (body.Any(key => !TransitionTable.IsTerminal(states[key])))
                return new(body.ToImmutableArray(), []);
        }

        foreach (var key in LifecycleStages.After.Where(keys.Contains))
            if (!TransitionTable.IsTerminal(states[key]))
                return Activate(key, states[key], changes);
        return new([], changes.ToImmutable(), true);
    }

    private static StagePlan Activate(string key, ExecutionStatus status,
        ImmutableArray<NodeStateChange>.Builder changes)
    {
        if (status == ExecutionStatus.Pending)
            changes.Add(new(key, ExecutionStatus.Ready));
        return new(status is ExecutionStatus.Pending or ExecutionStatus.Ready ? [key] : [], changes.ToImmutable());
    }
}
