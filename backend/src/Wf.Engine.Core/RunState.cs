using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core;

public sealed record RunSnapshot(Guid RunId, RunStatus Status, long LastSequence,
    ImmutableDictionary<string, ExecutionStatus> Nodes, ImmutableDictionary<string, ExecutionStatus> Tasks,
    WorkflowMetadata Metadata, ImmutableDictionary<string, JsonElement> Variables);

public sealed class RunState
{
    private readonly Action<string> log;
    private readonly List<RunEvent> events = [];
    public RunSnapshot Snapshot { get; private set; }
    public ImmutableArray<RunEvent> Events => events.ToImmutableArray();

    public RunState(Guid runId, ImmutableArray<NodeDefinition> nodes, WorkflowMetadata metadata,
        Action<string> log, IReadOnlyDictionary<string, JsonElement>? variables = null)
        : this(new(runId, RunStatus.Queued, 0,
            nodes.ToImmutableDictionary(n => n.Key, _ => ExecutionStatus.Pending, StringComparer.Ordinal),
            nodes.SelectMany(n => n.Tasks.Select(t => $"{n.Key}/{t.Key}"))
                .ToImmutableDictionary(key => key, _ => ExecutionStatus.Pending, StringComparer.Ordinal),
            metadata, variables?.ToImmutableDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) ??
                ImmutableDictionary<string, JsonElement>.Empty), log)
    {
    }

    private RunState(RunSnapshot snapshot, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Metadata);
        ArgumentNullException.ThrowIfNull(snapshot.Nodes);
        ArgumentNullException.ThrowIfNull(snapshot.Tasks);
        ArgumentNullException.ThrowIfNull(snapshot.Variables);
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
            Variables = snapshot.Variables.ToImmutableDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal)
        };
    }

    public static RunState Restore(RunSnapshot snapshot, IEnumerable<RunEvent> tail, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(tail);
        var run = new RunState(snapshot, log);
        foreach (var @event in tail)
            run.Apply(@event);
        return run;
    }

    public RunEvent CreateEvent(EventEntity entity, string? key, int state, DateTimeOffset at)
    {
        var @event = new RunEvent(Snapshot.RunId, checked(Snapshot.LastSequence + 1), at.ToUniversalTime(), entity, key, state);
        Validate(@event);
        return @event;
    }

    public RunEvent Append(EventEntity entity, string? key, int state, DateTimeOffset at)
    {
        var @event = CreateEvent(entity, key, state, at);
        Apply(@event);
        return @event;
    }

    public void Apply(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Validate(@event);
        Snapshot = @event.Entity switch
        {
            EventEntity.Run => Snapshot with { Status = (RunStatus)@event.State, LastSequence = @event.Sequence },
            EventEntity.Node => Snapshot with
                { Nodes = Snapshot.Nodes.SetItem(@event.Key!, (ExecutionStatus)@event.State), LastSequence = @event.Sequence },
            EventEntity.Task => Snapshot with
                { Tasks = Snapshot.Tasks.SetItem(@event.Key!, (ExecutionStatus)@event.State), LastSequence = @event.Sequence },
            _ => throw new ArgumentOutOfRangeException(nameof(@event))
        };
        events.Add(@event);
    }

    private void Validate(RunEvent @event)
    {
        if (@event.RunId != Snapshot.RunId || @event.Sequence != checked(Snapshot.LastSequence + 1))
            Reject($"Run '{Snapshot.RunId}' expected event sequence {Snapshot.LastSequence + 1} for the same run.");
        if (@event.At.Offset != TimeSpan.Zero)
            Reject("Event timestamps must use UTC.");
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
