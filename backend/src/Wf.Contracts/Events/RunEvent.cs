using System.Text.Json.Serialization;
using Wf.Contracts.Definition;
using Wf.Contracts.Progress;

namespace Wf.Contracts.Events;

public enum EventEntity { Run, Node, Task }

public sealed record RunEvent(Guid RunId, long Sequence, DateTimeOffset At, EventEntity Entity,
    string? Key, int State, AbortRecord? Abort = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RunQueuedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AttemptEndedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionStatus? AttemptStatus { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FailureCategory? FailureCategory { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WaitingReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AgentId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PlannedAgentId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteOperationProgress? Progress { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConditionOutcome? ConditionOutcome { get; init; }

    public void ValidateCondition()
    {
        if (ConditionOutcome is not { } outcome) return;
        if (!Enum.IsDefined(outcome) || Entity == EventEntity.Run || Key is null ||
            Progress is not null || Attempt is not null || AgentId is not null || WaitingReason is not null ||
            Abort is not null || AttemptStatus is not null || AttemptEndedAt is not null ||
            SelectionReason is not null || PlannedAgentId is not null ||
            !(outcome switch
            {
                Definition.ConditionOutcome.Matched => State == (int)ExecutionStatus.Ready && FailureCategory is null,
                Definition.ConditionOutcome.NotMatched => State == (int)ExecutionStatus.Skipped && FailureCategory is null,
                _ => (State == (int)ExecutionStatus.Failed || State == (int)ExecutionStatus.SoftFailed) &&
                    FailureCategory == Definition.FailureCategory.ValidationError
            }))
            throw new ArgumentException("A condition decision requires a ready, skipped or validation-failed entity without an attempt.");
    }

    public void ValidateSelection()
    {
        if (SelectionReason is null && PlannedAgentId is null) return;
        if (SelectionReason is not ("WarmHost" or "LeastLoaded" or "RoundRobin" or "Affinity") ||
            Entity != EventEntity.Task || State != (int)ExecutionStatus.Dispatched ||
            AgentId is null || AgentId == Guid.Empty || Attempt is null or <= 0 or > short.MaxValue ||
            PlannedAgentId == Guid.Empty || Progress is not null || WaitingReason is not null || ConditionOutcome is not null)
            throw new ArgumentException("Agent selection requires a dispatched attempt, known reason and valid agent identities.");
    }
}
