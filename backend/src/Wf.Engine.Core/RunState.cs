using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Runs;
using Wf.Engine.Core.Metrics;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core;

public sealed record RunSnapshot(Guid RunId, RunStatus Status, long LastSequence,
    ImmutableDictionary<string, ExecutionStatus> Nodes, ImmutableDictionary<string, ExecutionStatus> Tasks,
    WorkflowMetadata Metadata, ImmutableDictionary<string, JsonElement> Variables,
    AbortRecord? Abort = null, DateTimeOffset? EndedAt = null, ImmutableArray<string> AbortTargets = default)
{
    public RunMetrics? Metrics { get; init; }
    public string? FailureReason { get; init; }
    public RunPlacement? Placement { get; init; }
    public Guid? PlacementAgentId { get; init; }
    public ImmutableDictionary<string, string> TaskWaitReasons { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

public sealed class RunState
{
    private readonly Action<string> log;
    private readonly List<RunEvent> events = [];
    public RunSnapshot Snapshot { get; private set; }
    public ImmutableArray<RunEvent> Events => events.ToImmutableArray();

    public RunState(Guid runId, ImmutableArray<NodeDefinition> nodes, WorkflowMetadata metadata,
        Action<string> log, IReadOnlyDictionary<string, JsonElement>? variables = null)
        : this(runId, nodes, metadata, log, DateTimeOffset.UtcNow, variables)
    {
    }

    public RunState(Guid runId, ImmutableArray<NodeDefinition> nodes, WorkflowMetadata metadata,
        Action<string> log, DateTimeOffset queuedAt, IReadOnlyDictionary<string, JsonElement>? variables = null)
        : this(new(runId, RunStatus.Queued, 0,
            nodes.ToImmutableDictionary(n => n.Key, _ => ExecutionStatus.Pending, StringComparer.Ordinal),
            nodes.SelectMany(n => n.Tasks.Select(t => $"{n.Key}/{t.Key}"))
                .ToImmutableDictionary(key => key, _ => ExecutionStatus.Pending, StringComparer.Ordinal),
            metadata, variables?.ToImmutableDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) ??
                ImmutableDictionary<string, JsonElement>.Empty), log)
    {
        Snapshot = Snapshot with { Metrics = RunMetricsRecorder.Initialize(Snapshot, queuedAt) };
    }

    private RunState(RunSnapshot snapshot, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Metadata);
        ArgumentNullException.ThrowIfNull(snapshot.Nodes);
        ArgumentNullException.ThrowIfNull(snapshot.Tasks);
        ArgumentNullException.ThrowIfNull(snapshot.Variables);
        ArgumentNullException.ThrowIfNull(snapshot.TaskWaitReasons);
        if (snapshot.Placement is { } placement &&
            (placement.Machine is { } machine && (!placement.SingleMachine || string.IsNullOrWhiteSpace(machine) ||
                machine.Length > 200 || machine.Any(char.IsControl))) ||
            snapshot.PlacementAgentId is { } pinned && (pinned == Guid.Empty || snapshot.Placement?.SingleMachine != true) ||
            snapshot.TaskWaitReasons.Any(pair => pair.Value != "WaitingForMachine" ||
                !snapshot.Tasks.TryGetValue(pair.Key, out var state) || state != ExecutionStatus.Ready))
            throw new ArgumentException("Run snapshot contains invalid placement data.", nameof(snapshot));
        if (snapshot.FailureReason is { } failureReason &&
            (snapshot.Status != RunStatus.Failed || string.IsNullOrWhiteSpace(failureReason) || failureReason.Length > 500))
            throw new ArgumentException("A failure reason requires a failed run and 1-500 nonblank characters.", nameof(snapshot));
        if ((snapshot.Abort is not null && snapshot.Status is not (RunStatus.Aborting or RunStatus.Aborted)) ||
            (snapshot.EndedAt is { } endedAt && (endedAt.Offset != TimeSpan.Zero ||
                !TransitionTable.IsTerminal(snapshot.Status) || endedAt < snapshot.Abort?.At)) ||
            (!snapshot.AbortTargets.IsDefault && snapshot.AbortTargets.Any(key => !snapshot.Tasks.ContainsKey(key))))
            throw new ArgumentException("Run snapshot contains invalid abort or completion data.", nameof(snapshot));
        if (snapshot.RunId == Guid.Empty || snapshot.LastSequence < 0 || !Enum.IsDefined(snapshot.Status) ||
            snapshot.Nodes.Any(p => string.IsNullOrWhiteSpace(p.Key) || !Enum.IsDefined(p.Value)) ||
            snapshot.Tasks.Any(p => !Enum.IsDefined(p.Value) || p.Key.IndexOf('/') < 1 ||
                p.Key.EndsWith('/') || !snapshot.Nodes.ContainsKey(p.Key[..p.Key.IndexOf('/')])))
            throw new ArgumentException("Run snapshot contains invalid identity, sequence or entity state.", nameof(snapshot));
        this.log = log;
        Snapshot = snapshot with
        {
            Nodes = snapshot.Nodes.WithComparers(StringComparer.Ordinal),
            Tasks = snapshot.Tasks.WithComparers(StringComparer.Ordinal),
            AbortTargets = snapshot.AbortTargets.IsDefault ? [] : snapshot.AbortTargets,
            Variables = snapshot.Variables.ToImmutableDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal)
        };
        Snapshot = Snapshot with { Metrics = snapshot.Metrics ?? RunMetricsRecorder.Initialize(Snapshot) };
    }

    public static RunState Restore(RunSnapshot snapshot, IEnumerable<RunEvent> tail, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(tail);
        var run = new RunState(snapshot, log);
        foreach (var @event in tail)
            run.Apply(@event);
        return run;
    }

    public RunEvent CreateEvent(EventEntity entity, string? key, int state, DateTimeOffset at, AbortRecord? abort = null)
    {
        var @event = new RunEvent(Snapshot.RunId, checked(Snapshot.LastSequence + 1), at.ToUniversalTime(), entity, key, state, abort)
        {
            RunQueuedAt = Snapshot.LastSequence == 0 ? Snapshot.Metrics!.Run.QueuedAt : null
        };
        Validate(@event);
        return @event;
    }

    public RunEvent Append(EventEntity entity, string? key, int state, DateTimeOffset at, AbortRecord? abort = null)
    {
        var @event = CreateEvent(entity, key, state, at, abort);
        Apply(@event);
        return @event;
    }

    public void Apply(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Validate(@event);
        if (@event.Progress is not null)
        {
            Snapshot = Snapshot with { LastSequence = @event.Sequence };
            events.Add(@event);
            return;
        }
        var metrics = RunMetricsRecorder.Record(Snapshot.Metrics!, @event);
        Snapshot = @event.Entity switch
        {
            EventEntity.Run => Snapshot with
            {
                Status = (RunStatus)@event.State, LastSequence = @event.Sequence,
                Abort = @event.Abort ?? Snapshot.Abort,
                AbortTargets = @event.Abort is not null
                    ? Snapshot.Tasks.Where(p => p.Value is ExecutionStatus.Dispatched or ExecutionStatus.Running or ExecutionStatus.Retrying)
                        .Select(p => p.Key).Order(StringComparer.Ordinal).ToImmutableArray()
                    : Snapshot.AbortTargets,
                EndedAt = TransitionTable.IsTerminal((RunStatus)@event.State) ? @event.At : null,
                FailureReason = @event.FailureReason ?? Snapshot.FailureReason
            },
            EventEntity.Node => Snapshot with
                { Nodes = Snapshot.Nodes.SetItem(@event.Key!, (ExecutionStatus)@event.State), LastSequence = @event.Sequence },
            EventEntity.Task => Snapshot with
            {
                Tasks = Snapshot.Tasks.SetItem(@event.Key!, (ExecutionStatus)@event.State), LastSequence = @event.Sequence,
                TaskWaitReasons = @event.WaitingReason is { } reason
                    ? Snapshot.TaskWaitReasons.SetItem(@event.Key!, reason) : Snapshot.TaskWaitReasons.Remove(@event.Key!),
                PlacementAgentId = Snapshot.Placement?.SingleMachine == true && @event.AgentId is { } agent
                    ? agent : Snapshot.PlacementAgentId
            },
            _ => throw new ArgumentOutOfRangeException(nameof(@event))
        };
        Snapshot = Snapshot with { Metrics = metrics };
        events.Add(@event);
    }

    private void Validate(RunEvent @event)
    {
        if (@event.RunId != Snapshot.RunId || @event.Sequence != checked(Snapshot.LastSequence + 1))
            Reject($"Run '{Snapshot.RunId}' expected event sequence {Snapshot.LastSequence + 1} for the same run.");
        if (@event.At.Offset != TimeSpan.Zero)
            Reject("Event timestamps must use UTC.");
        try { @event.ValidateSelection(); }
        catch (ArgumentException) { Reject("Agent selection metadata is invalid."); }
        if (@event.Progress is { } progress)
        {
            try { Wf.Contracts.Progress.TaskProgressProtocol.Validate(progress); }
            catch (ArgumentException) { Reject("Remote progress metadata is invalid."); }
            if (@event.Entity != EventEntity.Task || @event.Key is null ||
                !Snapshot.Tasks.TryGetValue(@event.Key, out var current) ||
                current is not (ExecutionStatus.Dispatched or ExecutionStatus.Running) || @event.State != (int)current ||
                @event.Attempt is null || !Snapshot.Metrics!.Tasks.TryGetValue(@event.Key, out var progressMetric) ||
                progressMetric.Attempts.Length == 0 || progressMetric.Attempts[^1].Attempt != @event.Attempt ||
                @event.Abort is not null || @event.AgentId is not null || @event.WaitingReason is not null ||
                @event.RunQueuedAt is not null || @event.AttemptEndedAt is not null || @event.AttemptStatus is not null ||
                @event.FailureCategory is not null || @event.FailureReason is not null ||
                TransitionTable.IsTerminal(Snapshot.Status) || Snapshot.Abort is { } progressAbort && @event.At < progressAbort.At)
                Reject("Remote progress requires the current active task attempt without a state transition.");
            return;
        }
        if (@event.WaitingReason is not null && (@event.WaitingReason != "WaitingForMachine" ||
                @event.Entity != EventEntity.Task || (ExecutionStatus)@event.State != ExecutionStatus.Ready) ||
            @event.AgentId is { } agent && (agent == Guid.Empty || @event.Entity != EventEntity.Task ||
                (ExecutionStatus)@event.State != ExecutionStatus.Dispatched))
            Reject("Placement metadata requires a waiting Ready task or a dispatched task with a nonempty agent ID.");
        if (@event.FailureReason is { } reason &&
            (string.IsNullOrWhiteSpace(reason) || reason.Length > 500 ||
             @event.Entity != EventEntity.Run || (RunStatus)@event.State != RunStatus.Failed))
            Reject("A failure reason requires a failed run and 1-500 nonblank characters.");
        if (@event.FailureCategory is { } category && (!Enum.IsDefined(category) ||
            !(@event.Entity switch
            {
                EventEntity.Run => (RunStatus)@event.State is RunStatus.Failed or RunStatus.TimedOut or RunStatus.Aborting or RunStatus.Aborted,
                EventEntity.Node => (ExecutionStatus)@event.State is ExecutionStatus.Failed or ExecutionStatus.SoftFailed or ExecutionStatus.TimedOut or ExecutionStatus.Cancelled,
                EventEntity.Task => (ExecutionStatus)@event.State is ExecutionStatus.Retrying or ExecutionStatus.Failed or ExecutionStatus.SoftFailed or ExecutionStatus.TimedOut or ExecutionStatus.Cancelled,
                _ => false
            })))
            Reject("Failure categories require a known category on a failed, cancelled, aborting or retrying entity.");
        if (@event.RunQueuedAt is { } queuedAt && (queuedAt.Offset != TimeSpan.Zero || @event.Sequence != 1))
            Reject("Run queue time can only be recorded in UTC on the first event.");
        if (@event.Attempt is { } attempt && (@event.Entity != EventEntity.Task || attempt is <= 0 or > short.MaxValue))
            Reject("An attempt identity requires a task and positive smallint attempt number.");
        if (@event.Attempt is { } number && @event.Key is { } attemptKey &&
            Snapshot.Metrics!.Tasks.TryGetValue(attemptKey, out var taskMetric) &&
            taskMetric.Attempts is { Length: > 0 } previous &&
            ((ExecutionStatus)@event.State == ExecutionStatus.Dispatched
                ? number <= previous[^1].Attempt : number != previous[^1].Attempt))
            Reject("An event must identify the current attempt, or a newer dispatched attempt.");
        if ((@event.AttemptEndedAt is { } attemptEnd && (attemptEnd.Offset != TimeSpan.Zero || attemptEnd > @event.At)) ||
            (@event.AttemptStatus is { } attemptStatus && !TransitionTable.IsTerminal(attemptStatus)) ||
            ((@event.AttemptEndedAt is not null || @event.AttemptStatus is not null) &&
                (@event.Entity != EventEntity.Task || ((ExecutionStatus)@event.State != ExecutionStatus.Retrying &&
                    !TransitionTable.IsTerminal((ExecutionStatus)@event.State)))))
            Reject("Attempt completion data requires a UTC end time and terminal outcome on a task completion or retry.");
        if (@event.AttemptEndedAt is { } completion && @event.Key is { } taskKey &&
            Snapshot.Metrics!.Tasks.TryGetValue(taskKey, out var metric) && metric.Attempts is { Length: > 0 } attempts &&
            completion < (attempts[^1].StartedAt ?? attempts[^1].QueuedAt))
            Reject("An attempt cannot end before it started or entered the queue.");
        if (@event.Abort is { } abort && (@event.Entity != EventEntity.Run || Snapshot.Abort is not null ||
                (Snapshot.Status, (RunStatus)@event.State) is not
                    ((RunStatus.Queued, RunStatus.Aborted) or (RunStatus.Running, RunStatus.Aborting)) ||
                abort.At > @event.At))
            Reject("An abort record can only be set once, on the initial abort transition, at or after acceptance.");
        if (Snapshot.Abort is { } accepted && @event.At < accepted.At)
            Reject("An event cannot precede the accepted abort time.");
        if (TransitionTable.IsTerminal(Snapshot.Status))
            Reject($"Run '{Snapshot.RunId}' is terminal ({Snapshot.Status}); late events are rejected.");
        switch (@event.Entity)
        {
            case EventEntity.Run:
                if (@event.Key is not null)
                    Reject("Run events cannot specify an entity key.");
                TransitionTable.Require(Snapshot.Status, (RunStatus)@event.State, log);
                break;
            case EventEntity.Node:
            case EventEntity.Task:
                var entities = @event.Entity == EventEntity.Node ? Snapshot.Nodes : Snapshot.Tasks;
                if (@event.Key is null || !entities.ContainsKey(@event.Key))
                    Reject($"Unknown {@event.Entity} key '{@event.Key}'.");
                if (!(@event.Entity == EventEntity.Task && @event.WaitingReason is not null &&
                    entities[@event.Key!] == ExecutionStatus.Ready &&
                    !Snapshot.TaskWaitReasons.ContainsKey(@event.Key!)))
                    TransitionTable.Require(entities[@event.Key!], (ExecutionStatus)@event.State, log);
                break;
            default:
                Reject($"Unknown event entity '{@event.Entity}'.");
                break;
        }
    }

    private void Reject(string message)
    {
        log(message);
        throw new InvalidOperationException(message);
    }
}
