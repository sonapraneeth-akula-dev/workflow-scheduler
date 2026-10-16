using System.Text.Json.Serialization;

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
}
