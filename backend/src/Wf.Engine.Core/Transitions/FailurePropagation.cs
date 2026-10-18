using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Transitions;

public sealed record TaskStateChange(string Task, ExecutionStatus Status);
public sealed record TaskFailurePlan(ImmutableArray<TaskStateChange> Changes, ImmutableArray<string> CancelRunning);

public static class FailurePropagation
{
    public static TaskFailurePlan Plan(NodeDefinition node, string failedTask,
        IReadOnlyDictionary<string, ExecutionStatus> states, bool timedOut = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(states);
        ValidateStates(node, states);
        var task = node.Tasks.SingleOrDefault(t => t.Key == failedTask) ??
            throw new ArgumentException($"Node '{node.Key}' has no task '{failedTask}'.", nameof(failedTask));
        var outcome = task.Failure == FailurePolicy.Soft ? ExecutionStatus.SoftFailed :
            timedOut ? ExecutionStatus.TimedOut : ExecutionStatus.Failed;
        if (!TransitionTable.CanTransition(states[failedTask], outcome))
            throw new InvalidOperationException($"Task '{node.Key}/{failedTask}' cannot fail from {states[failedTask]}.");
        var changes = ImmutableArray.CreateBuilder<TaskStateChange>();
        changes.Add(new(failedTask, outcome));
        var cancel = ImmutableArray.CreateBuilder<string>();
        if (task.Failure == FailurePolicy.Hard)
            foreach (var sibling in node.Tasks.Where(t => t.Key != failedTask))
            {
                var state = states[sibling.Key];
                if (state is ExecutionStatus.Pending or ExecutionStatus.Ready or ExecutionStatus.Retrying)
                    changes.Add(new(sibling.Key, ExecutionStatus.Cancelled));
                else if (state is ExecutionStatus.Dispatched or ExecutionStatus.Running)
                    cancel.Add(sibling.Key);
            }
        return new(changes.ToImmutable(), cancel.ToImmutable());
    }

    public static ExecutionStatus? ResolveNode(NodeDefinition node, IReadOnlyDictionary<string, ExecutionStatus> states)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(states);
        ValidateStates(node, states);
        var taskStates = node.Tasks.Select(t => states[t.Key]).ToArray();
        if (taskStates.Any(s => !TransitionTable.IsTerminal(s)))
            return null;
        if (taskStates.Any(s => s is ExecutionStatus.Failed or ExecutionStatus.TimedOut))
            return node.Failure == FailurePolicy.Soft ? ExecutionStatus.SoftFailed :
                taskStates.Contains(ExecutionStatus.Failed) ? ExecutionStatus.Failed : ExecutionStatus.TimedOut;
        if (taskStates.Contains(ExecutionStatus.Cancelled))
            return ExecutionStatus.Cancelled;
        if (taskStates.Contains(ExecutionStatus.SoftFailed))
            return ExecutionStatus.SoftFailed;
        if (taskStates.Length > 0 && taskStates.All(s => s == ExecutionStatus.Skipped))
            return ExecutionStatus.Skipped;
        return ExecutionStatus.Succeeded;
    }

    private static void ValidateStates(NodeDefinition node, IReadOnlyDictionary<string, ExecutionStatus> states)
    {
        foreach (var task in node.Tasks)
            if (!states.TryGetValue(task.Key, out var state) || !Enum.IsDefined(state))
                throw new ArgumentException($"Task '{node.Key}/{task.Key}' has no valid state.", nameof(states));
    }
}
