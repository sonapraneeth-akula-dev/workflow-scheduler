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

    public void ValidateSelection()
    {
        if (SelectionReason is null && PlannedAgentId is null) return;
        if (SelectionReason is not ("WarmHost" or "LeastLoaded" or "RoundRobin" or "Affinity") ||
            Entity != EventEntity.Task || State != (int)ExecutionStatus.Dispatched ||
            AgentId is null || AgentId == Guid.Empty || Attempt is null or <= 0 or > short.MaxValue ||
            PlannedAgentId == Guid.Empty || Progress is not null || WaitingReason is not null)
            throw new ArgumentException("Agent selection requires a dispatched attempt, known reason and valid agent identities.");
    }
}
