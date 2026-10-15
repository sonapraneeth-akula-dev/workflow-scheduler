using System.Threading.Channels;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent;

public sealed class AgentOutbox
{
    private readonly object gate = new();
    private readonly SortedDictionary<ulong, AgentMessage> pending = [];
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private ulong sequence;

    public AgentMessage CreateHello(Hello hello)
    {
        lock (gate)
            return new AgentMessage
            {
                Sequence = pending.Count == 0 ? checked(++sequence) : pending.First().Key - 1,
                Hello = hello
            };
    }

    public void Enqueue(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PayloadCase is not (AgentMessage.PayloadOneofCase.TaskStarted or
            AgentMessage.PayloadOneofCase.TaskCompleted or AgentMessage.PayloadOneofCase.Heartbeat))
            throw new ArgumentException("Only task events and heartbeats belong in the agent outbox.", nameof(message));
        lock (gate)
        {
            if (sequence == 0) sequence = 1;
            var copy = message.Clone();
            copy.Sequence = checked(++sequence);
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
        lock (gate)
        {
            if (acknowledgement.Sequence == 0 || acknowledgement.Sequence > sequence || acknowledgement.LeaderEpoch != 0)
                throw new InvalidDataException("Invalid acknowledgement of agent messages.");
            foreach (var key in pending.Keys.TakeWhile(key => key <= acknowledgement.Sequence).ToArray())
                pending.Remove(key);
        }
    }

    public async Task WaitForChangeAsync(CancellationToken cancellationToken) =>
        _ = await changed.Reader.ReadAsync(cancellationToken);
}
