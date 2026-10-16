using System.Text;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Engine.Core.StateMachine;
using Wf.Contracts.Outputs;

namespace Wf.Engine.Core.Results;

public readonly record struct ResultIdentity(Guid RunId, string TaskKey, short Attempt);

/// <summary>Uses the persisted attempt status under the caller's transaction and session/lease fence, not a volatile cache.</summary>
public static class ResultDeduplicator
{
    public static ResultIdentity Identity(AttemptRef? reference)
    {
        if (reference is null || !Guid.TryParse(reference.RunId, out var runId) || runId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reference.NodeKey) || reference.NodeKey.Contains('/') ||
            string.IsNullOrWhiteSpace(reference.TaskKey) || reference.TaskKey.Contains('/') ||
            Encoding.UTF8.GetByteCount(reference.NodeKey) > 512 || Encoding.UTF8.GetByteCount(reference.TaskKey) > 512 ||
            reference.Attempt is 0 or > 32767)
            throw new ArgumentException("Task event requires a valid attempt.", nameof(reference));
        return new ResultIdentity(runId, $"{reference.NodeKey}/{reference.TaskKey}", (short)reference.Attempt);
    }

    public static bool IsDuplicate(ExecutionStatus persistedStatus, bool completion) =>
        TransitionTable.IsTerminal(persistedStatus) || !completion && persistedStatus == ExecutionStatus.Running;

    public static void Validate(TaskCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        _ = Identity(completed.Task);
        if (completed.At is null) throw new ArgumentException("Task result requires a timestamp.", nameof(completed));
        try { _ = completed.At.ToDateTimeOffset(); }
        catch (InvalidOperationException exception) { throw new ArgumentException("Invalid result timestamp.", nameof(completed), exception); }
        if (completed.Outcome == TaskOutcome.Unspecified || !Enum.IsDefined(completed.Outcome) ||
            Encoding.UTF8.GetByteCount(completed.Error?.Message ?? "") > 4096)
            throw new ArgumentException("Task result contains an invalid outcome or oversized payload.", nameof(completed));
        if (TaskOutputJson.Error(completed.OutputsJson.Memory) is { } error)
            throw new ArgumentException(error, nameof(completed));
    }
}
