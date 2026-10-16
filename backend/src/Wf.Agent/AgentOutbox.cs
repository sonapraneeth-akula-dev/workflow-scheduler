using System.Threading.Channels;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent;

public sealed class AgentOutbox : IDisposable
{
    private readonly object gate = new();
    private readonly SortedDictionary<ulong, AgentMessage> pending = [];
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private ulong sequence;
    private ulong leaderEpoch;
    private ulong reservationEpoch;
    private readonly ResultJournal? journal;

    public AgentOutbox(ResultJournal? journal = null)
    {
        this.journal = journal;
        sequence = journal?.Sequence ?? 0;
        leaderEpoch = journal?.LeaderEpoch ?? 0;
        reservationEpoch = journal?.ReservationEpoch ?? 1;
        foreach (var message in journal?.Pending ?? [])
            pending.Add(message.Sequence, message.Clone());
    }

    public ulong ReservationEpoch { get { lock (gate) return reservationEpoch; } }

    public AgentMessage CreateHello(Hello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        lock (gate)
        {
            var next = pending.Count == 0 ? checked(sequence + 1) : pending.First().Key - 1;
            if (next > sequence)
            {
                Persist(next, leaderEpoch, pending.Values);
                sequence = next;
            }
            return new AgentMessage
            {
                Sequence = next,
                Hello = hello
            };
        }
    }

    public void Enqueue(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PayloadCase is not (AgentMessage.PayloadOneofCase.TaskStarted or
            AgentMessage.PayloadOneofCase.TaskCompleted or AgentMessage.PayloadOneofCase.Heartbeat or
            AgentMessage.PayloadOneofCase.ResourceSample or AgentMessage.PayloadOneofCase.ReservationFenced))
            throw new ArgumentException("Only task events, heartbeats and fence acknowledgements belong in the agent outbox.", nameof(message));
        lock (gate)
        {
            var copy = message.Clone();
            copy.Sequence = checked(Math.Max(sequence, 1) + 1);
            if (journal is not null && copy.Heartbeat is null) ResultJournal.Validate(copy);
            Persist(copy.Sequence, leaderEpoch, pending.Values.Append(copy));
            sequence = copy.Sequence;
            // Only the latest heartbeat is useful after a disconnection; task events are never dropped.
            if (copy.Heartbeat is not null)
                foreach (var key in pending.Where(pair => pair.Value.Heartbeat is not null).Select(pair => pair.Key).ToArray())
                    pending.Remove(key);
            pending.Add(copy.Sequence, copy);
        }
        changed.Writer.TryWrite(true);
    }

    public AgentMessage? NextAfter(ulong sent)
    {
        lock (gate)
            return pending.FirstOrDefault(pair => pair.Key > sent).Value?.Clone();
    }

    public void Acknowledge(Ack acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        lock (gate)
        {
            if (acknowledgement.Sequence == 0 || acknowledgement.Sequence > sequence || acknowledgement.LeaderEpoch != 0)
                throw new InvalidDataException("Invalid acknowledgement of agent messages.");
            Persist(sequence, leaderEpoch, pending.Where(pair => pair.Key > acknowledgement.Sequence).Select(pair => pair.Value));
            foreach (var key in pending.Keys.TakeWhile(key => key <= acknowledgement.Sequence).ToArray())
                pending.Remove(key);
        }
    }

    public void ObserveLeaderEpoch(ulong epoch)
    {
        lock (gate)
        {
            if (epoch == 0 || epoch < leaderEpoch) throw new InvalidDataException("Control message is from a stale leader.");
            if (epoch == leaderEpoch) return;
            Persist(sequence, epoch, pending.Values);
            leaderEpoch = epoch;
        }
    }

    public bool ContainsResult(AttemptRef task)
    {
        lock (gate)
            return pending.Values.Any(message => message.TaskCompleted?.Task is { } result &&
                Guid.TryParse(result.RunId, out var run) && Guid.TryParse(task.RunId, out var expected) &&
                run == expected && result.NodeKey == task.NodeKey && result.TaskKey == task.TaskKey && result.Attempt == task.Attempt);
    }

    public void ObserveReservationEpoch(ulong epoch)
    {
        lock (gate)
        {
            if (epoch == 0 || epoch < reservationEpoch) throw new InvalidDataException("Command is from a stale machine reservation.");
            if (epoch == reservationEpoch) return;
            journal?.Save(sequence, leaderEpoch, pending.Values.Where(message => message.Heartbeat is null).ToArray(), epoch);
            reservationEpoch = epoch;
        }
    }

    public IReadOnlyList<AttemptRef> UnacknowledgedAttempts()
    {
        lock (gate)
            return pending.Values.Select(message => message.TaskStarted?.Task ?? message.TaskCompleted?.Task ?? message.ResourceSample?.Task)
                .OfType<AttemptRef>().Select(task => task.Clone()).ToArray();
    }

    private void Persist(ulong next, ulong epoch, IEnumerable<AgentMessage> messages) =>
        journal?.Save(next, epoch, messages.Where(message => message.Heartbeat is null).ToArray(), reservationEpoch);

    public void Dispose() => journal?.Dispose();

    public async Task WaitForChangeAsync(CancellationToken cancellationToken) =>
        _ = await changed.Reader.ReadAsync(cancellationToken);
}
