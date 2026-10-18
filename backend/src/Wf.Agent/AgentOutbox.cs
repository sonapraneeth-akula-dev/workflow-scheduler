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
    private readonly Dictionary<ulong, TaskCompletionSource> checkpointWaiters = [];
    private readonly Channel<LiveLogBatch> logs = Channel.CreateBounded<LiveLogBatch>(32);
    private bool disposed;

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
            while (logs.Reader.TryRead(out _)) { }
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

    public void Enqueue(AgentMessage message) => EnqueueCore(message);

    internal bool TryPublishLogs(LiveLogBatch batch)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!logs.Writer.TryWrite(batch.Clone())) return false;
            changed.Writer.TryWrite(true);
            return true;
        }
    }

    internal AgentMessage? TakeLogs()
    {
        lock (gate) return logs.Reader.TryRead(out var batch) ? new() { LiveLogs = batch } : null;
    }

    internal void DropLogs(AttemptRef? task = null)
    {
        lock (gate)
        {
            var retained = new List<LiveLogBatch>();
            while (logs.Reader.TryRead(out var batch))
                if (task is not null && !batch.Task.Equals(task)) retained.Add(batch);
            foreach (var batch in retained)
                if (!logs.Writer.TryWrite(batch)) throw new InvalidOperationException("Cannot restore the bounded live log queue.");
        }
    }

    public void PublishProgress(TaskProgress progress)
    {
        if (journal is null) throw new InvalidOperationException("Task progress requires a durable agent journal.");
        EnqueueCore(new() { TaskProgress = progress });
    }

    public async Task PublishCheckpointAsync(CheckpointSaved checkpoint, CancellationToken cancellationToken = default)
    {
        if (journal is null) throw new InvalidOperationException("Checkpoint acknowledgement requires a durable agent journal.");
        cancellationToken.ThrowIfCancellationRequested();
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = EnqueueCore(new() { CheckpointSaved = checkpoint }, acknowledgement);
        try { await acknowledgement.Task.WaitAsync(cancellationToken); }
        finally { lock (gate) checkpointWaiters.Remove(id); }
    }

    private ulong EnqueueCore(AgentMessage message, TaskCompletionSource? acknowledgement = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PayloadCase is not (AgentMessage.PayloadOneofCase.TaskStarted or
            AgentMessage.PayloadOneofCase.TaskCompleted or AgentMessage.PayloadOneofCase.Heartbeat or
            AgentMessage.PayloadOneofCase.ResourceSample or AgentMessage.PayloadOneofCase.ReservationFenced or
            AgentMessage.PayloadOneofCase.CalibrationCompleted or AgentMessage.PayloadOneofCase.CheckpointSaved or
            AgentMessage.PayloadOneofCase.TaskProgress or AgentMessage.PayloadOneofCase.DrainCompleted))
            throw new ArgumentException("Only task events, heartbeats, calibration results, checkpoints, drain and fence acknowledgements belong in the agent outbox.", nameof(message));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var copy = message.Clone();
            copy.Sequence = checked(Math.Max(sequence, 1) + 1);
            if ((journal is not null && copy.Heartbeat is null) || copy.CalibrationCompleted is not null ||
                copy.CheckpointSaved is not null || copy.TaskProgress is not null || copy.DrainCompleted is not null) ResultJournal.Validate(copy);
            Persist(copy.Sequence, leaderEpoch, pending.Values.Append(copy));
            sequence = copy.Sequence;
            // Only the latest heartbeat is useful after a disconnection; task events are never dropped.
            if (copy.Heartbeat is not null)
                foreach (var key in pending.Where(pair => pair.Value.Heartbeat is not null).Select(pair => pair.Key).ToArray())
                    pending.Remove(key);
            pending.Add(copy.Sequence, copy);
            if (acknowledgement is not null) checkpointWaiters.Add(copy.Sequence, acknowledgement);
            changed.Writer.TryWrite(true);
            return copy.Sequence;
        }
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
            foreach (var key in checkpointWaiters.Keys.Where(key => key <= acknowledgement.Sequence).ToArray())
            {
                checkpointWaiters[key].TrySetResult();
                checkpointWaiters.Remove(key);
            }
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

    public bool ContainsCalibrationResult(string id, ulong epoch)
    {
        lock (gate)
            return pending.Values.Any(message => message.CalibrationCompleted is { } result &&
                Guid.Parse(result.Id) == Guid.Parse(id) && result.ReservationEpoch == epoch);
    }

    public IReadOnlyList<AttemptRef> UnacknowledgedAttempts()
    {
        lock (gate)
            return pending.Values.Select(message => message.TaskStarted?.Task ?? message.TaskCompleted?.Task ??
                message.ResourceSample?.Task ?? message.CheckpointSaved?.Task ?? message.TaskProgress?.Task)
                .OfType<AttemptRef>().Select(task => task.Clone()).ToArray();
    }

    private void Persist(ulong next, ulong epoch, IEnumerable<AgentMessage> messages) =>
        journal?.Save(next, epoch, messages.Where(message => message.Heartbeat is null).ToArray(), reservationEpoch);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var waiter in checkpointWaiters.Values)
                waiter.TrySetException(new ObjectDisposedException(nameof(AgentOutbox)));
            checkpointWaiters.Clear();
            journal?.Dispose();
        }
    }

    public async Task WaitForChangeAsync(CancellationToken cancellationToken) =>
        _ = await changed.Reader.ReadAsync(cancellationToken);
}
