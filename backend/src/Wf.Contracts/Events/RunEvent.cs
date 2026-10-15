namespace Wf.Contracts.Events;

public enum EventEntity { Run, Node, Task }

public sealed record RunEvent(Guid RunId, long Sequence, DateTimeOffset At, EventEntity Entity,
    string? Key, int State, AbortRecord? Abort = null);
