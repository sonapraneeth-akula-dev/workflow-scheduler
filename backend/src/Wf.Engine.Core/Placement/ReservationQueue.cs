using System.Collections.Immutable;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Placement;

public enum ReservationDisposition { Reserved, Queued, Rejected }

public static class ReservationQueue
{
    public const string UnavailableReason = "ReservationUnavailable";

    public static ReservationDisposition Decide(WhenUnavailable policy, int position, bool available)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        return position == 1 && available ? ReservationDisposition.Reserved :
            policy == WhenUnavailable.Reject ? ReservationDisposition.Rejected : ReservationDisposition.Queued;
    }

    public static ImmutableArray<RunEvent> Reject(RunState run, string nodeKey, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.Snapshot.Nodes.TryGetValue(nodeKey, out var state) || TransitionTable.IsTerminal(state) ||
            run.Snapshot.Status is not (RunStatus.Queued or RunStatus.Running))
            throw new InvalidOperationException("Only an active run and node can reject an unavailable reservation.");
        var changes = ImmutableArray.CreateBuilder<RunEvent>();
        var copy = RunState.Restore(run.Snapshot, [], _ => { });
        if (copy.Snapshot.Status == RunStatus.Queued) Add(EventEntity.Run, null, (int)RunStatus.Running);
        foreach (var task in copy.Snapshot.Tasks.OrderBy(task => task.Key, StringComparer.Ordinal))
            if (!TransitionTable.IsTerminal(task.Value))
                Add(EventEntity.Task, task.Key, (int)ExecutionStatus.Cancelled);
        foreach (var node in copy.Snapshot.Nodes.OrderBy(node => node.Key, StringComparer.Ordinal))
        {
            if (TransitionTable.IsTerminal(node.Value)) continue;
            if (node.Key == nodeKey)
            {
                if (node.Value == ExecutionStatus.Pending) Add(EventEntity.Node, node.Key, (int)ExecutionStatus.Ready);
                Add(EventEntity.Node, node.Key, (int)ExecutionStatus.Failed);
            }
            else Add(EventEntity.Node, node.Key, (int)ExecutionStatus.Cancelled);
        }
        Add(EventEntity.Run, null, (int)RunStatus.Failed);
        return changes.ToImmutable();

        void Add(EventEntity entity, string? key, int status)
        {
            var change = copy.CreateEvent(entity, key, status, at) with
            {
                FailureCategory = entity == EventEntity.Run && status == (int)RunStatus.Running ||
                    entity == EventEntity.Node && status == (int)ExecutionStatus.Ready ? null : FailureCategory.Placement,
                FailureReason = entity == EventEntity.Run && status == (int)RunStatus.Failed ? UnavailableReason : null
            };
            copy.Apply(change);
            changes.Add(change);
        }
    }
}
