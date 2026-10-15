using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Stages;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Scheduling;

public static class ReadinessEvaluator
{
    public static ImmutableArray<NodeStateChange> Evaluate(ImmutableArray<NodeDefinition> nodes,
        IReadOnlyDictionary<string, ExecutionStatus> states, IEnumerable<string>? eligibleNodes = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (nodes.IsDefault)
            throw new ArgumentException("Nodes must be initialized.", nameof(nodes));
        var eligible = eligibleNodes?.ToHashSet(StringComparer.Ordinal);
        var changes = ImmutableArray.CreateBuilder<NodeStateChange>();
        foreach (var node in nodes)
        {
            if (!states.TryGetValue(node.Key, out var status) || !Enum.IsDefined(status))
                throw new ArgumentException($"Node '{node.Key}' has no valid state.", nameof(states));
            foreach (var dependency in node.Dependencies)
                if (!states.TryGetValue(dependency.Node, out var state) || !Enum.IsDefined(state))
                    throw new ArgumentException($"Node '{node.Key}' depends on unavailable node '{dependency.Node}'.", nameof(states));
            if (status != ExecutionStatus.Pending ||
                (eligible is not null ? !eligible.Contains(node.Key) : LifecycleStages.IsStage(node.Key)))
                continue;
            if (!node.Dependencies.All(d => TransitionTable.IsTerminal(states[d.Node])))
                continue;
            changes.Add(new(node.Key, node.Dependencies.All(d => IsSatisfied(d.When, states[d.Node]))
                ? ExecutionStatus.Ready : ExecutionStatus.Skipped));
        }
        return changes.ToImmutable();
    }

    public static bool IsSatisfied(DependencyCondition condition, ExecutionStatus status)
    {
        if (!Enum.IsDefined(condition))
            throw new ArgumentOutOfRangeException(nameof(condition));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        return condition switch
        {
            DependencyCondition.OnSuccess => status is ExecutionStatus.Succeeded or ExecutionStatus.SoftFailed or ExecutionStatus.Reused,
            DependencyCondition.OnFailure => status is ExecutionStatus.Failed or ExecutionStatus.TimedOut,
            DependencyCondition.Always => TransitionTable.IsTerminal(status),
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
    }
}
