using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;

namespace Wf.Agent.Logs;

public sealed class AgentLogMirror : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Watch> watches = new(StringComparer.Ordinal);
    private readonly AgentOutbox outbox;
    private readonly TimeProvider clock;
    private readonly ILogger<AgentLogMirror> logger;
    private readonly CancellationTokenSource stop = new();
    private readonly Task pump;

    public AgentLogMirror(AgentOutbox outbox, TimeProvider clock, ILogger<AgentLogMirror> logger)
    {
        this.outbox = outbox;
        this.clock = clock;
        this.logger = logger;
        pump = PumpAsync();
    }

    public void Set(MirrorLogs command)
    {
        LiveLogProtocol.Validate(command, clock.GetUtcNow());
        var id = LiveLogProtocol.Identity(command.Task);
        lock (gate)
        {
            if (command.ReservationEpoch != outbox.ReservationEpoch)
                throw new InvalidDataException("Log interest belongs to a stale machine reservation.");
            if (!command.On || command.ExpiresAt.ToDateTimeOffset() <= clock.GetUtcNow())
            {
                if (command.On) logger.LogWarning("Log interest expired before delivery for {Attempt}; mirroring stopped.", id);
                watches.Remove(id);
                outbox.DropLogs(command.Task);
                return;
            }
            if (!watches.TryGetValue(id, out var watch))
            {
                if (watches.Count >= 256) throw new InvalidDataException("Too many mirrored log attempts.");
                watches.Add(id, watch = new(command.Task.Clone(), command.ReservationEpoch));
            }
            watch.Expires = command.ExpiresAt.ToDateTimeOffset();
        }
    }

    public void Reset()
    {
        lock (gate) { watches.Clear(); outbox.DropLogs(); }
    }

    internal void Append(AttemptRef task, ulong epoch, long line, LogFrame frame)
    {
        var id = LiveLogProtocol.Identity(task);
        lock (gate)
        {
            if (!watches.TryGetValue(id, out var watch) || watch.Epoch != epoch ||
                epoch != outbox.ReservationEpoch || watch.Expires <= clock.GetUtcNow()) return;
            var record = new LiveLogRecord
            {
                Line = checked((ulong)line), At = Timestamp.FromDateTimeOffset(frame.At),
                Stream = frame.Stream, Level = frame.Level, Text = frame.Text
            };
            // Include the protobuf tag/length and attempt envelope in the byte ceiling.
            var bytes = record.CalculateSize() + 8;
            while (watch.Records.Count > 0 && (watch.Records.Count >= LiveLogProtocol.MaximumRecords ||
                watch.Bytes + bytes > LiveLogProtocol.MaximumBatchBytes - 2048))
            {
                watch.Bytes -= watch.Records.Dequeue().CalculateSize() + 8;
                watch.Gap = true;
            }
            watch.Records.Enqueue(record);
            watch.Bytes += bytes;
        }
    }

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(LiveLogProtocol.BatchInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
                Flush();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    internal void Flush()
    {
        lock (gate)
        {
            foreach (var pair in watches.ToArray())
            {
                var watch = pair.Value;
                if (watch.Expires <= clock.GetUtcNow() || watch.Epoch != outbox.ReservationEpoch)
                {
                    watches.Remove(pair.Key);
                    outbox.DropLogs(watch.Task);
                    continue;
                }
                if (watch.Records.Count == 0) continue;
                var batch = new LiveLogBatch { Task = watch.Task, ReservationEpoch = watch.Epoch, Gap = watch.Gap };
                batch.Records.Add(watch.Records);
                if (!outbox.TryPublishLogs(batch)) continue;
                watch.Records.Clear();
                watch.Bytes = 0;
                watch.Gap = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await pump;
        stop.Dispose();
    }

    private sealed class Watch(AttemptRef task, ulong epoch)
    {
        public AttemptRef Task { get; } = task;
        public ulong Epoch { get; } = epoch;
        public DateTimeOffset Expires { get; set; }
        public Queue<LiveLogRecord> Records { get; } = new();
        public int Bytes { get; set; }
        public bool Gap { get; set; }
    }
}
