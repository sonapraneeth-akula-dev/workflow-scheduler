using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Wf.Contracts.Checkpoints;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Secrets;

namespace Wf.Sdk;

public sealed record TaskAttempt(uint Number, DateTimeOffset? ResumedFrom = null);

public sealed class CheckpointStore
{
    private readonly Func<ReadOnlyMemory<byte>, long, DateTimeOffset, CancellationToken, Task>? persist;
    private readonly TimeProvider clock;
    private readonly CancellationToken attemptCancellation;
    private readonly SemaphoreSlim gate = new(1);
    private byte[]? state;
    private long sequence;
    private DateTimeOffset? savedAt;
    private long? savedTimestamp;
    private readonly object secretGate = new();
    private string[] secretPatterns = [];

    public CheckpointStore(
        Func<ReadOnlyMemory<byte>, long, DateTimeOffset, CancellationToken, Task>? persist = null,
        ReadOnlyMemory<byte> initialState = default, long sequence = 0, DateTimeOffset? savedAt = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        if (sequence < 0 || initialState.IsEmpty != (sequence == 0) || initialState.IsEmpty != (savedAt is null))
            throw new ArgumentException("Checkpoint seed requires state, a positive sequence and its saved time together.");
        if (!initialState.IsEmpty)
        {
            CheckpointProtocol.ValidateState(initialState);
            state = initialState.ToArray();
        }
        this.persist = persist;
        this.sequence = sequence;
        this.savedAt = savedAt;
        clock = timeProvider ?? TimeProvider.System;
        attemptCancellation = cancellationToken;
    }

    public T? TryLoad<T>(JsonTypeInfo<T>? typeInfo = null)
    {
        var snapshot = Volatile.Read(ref state);
        return snapshot is null ? default : JsonSerializer.Deserialize(snapshot, TypeInfo(typeInfo));
    }

    public Task SaveAsync<T>(T value, CancellationToken cancellationToken = default) =>
        SaveAsync(value, TypeInfo<T>(null), cancellationToken);

    internal void AddSecrets(IEnumerable<string> values)
    {
        var added = SecretPatterns.ForJson(values);
        if (added.Length == 0) return;
        lock (secretGate)
            Volatile.Write(ref secretPatterns, secretPatterns.Concat(added).Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task SaveAsync<T>(T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (persist is null)
            throw new NotSupportedException("Durable checkpoint publishing is not configured for this task host.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation, cancellationToken);
        using var bytes = new BoundedState();
        await JsonSerializer.SerializeAsync(bytes, value, typeInfo, stop.Token);
        var payload = bytes.ToArray();
        var patterns = Volatile.Read(ref secretPatterns);
        if (patterns.Length != 0)
        {
            using var document = JsonDocument.Parse(payload);
            if (SecretPatterns.Contains(document.RootElement, patterns))
                throw new TaskFailureException("Checkpoint state contains a resolved secret value.",
                    retryable: false, category: FailureCategory.ValidationError);
        }
        await gate.WaitAsync(stop.Token);
        try
        {
            while (savedAt is { } previous)
            {
                var delay = CheckpointProtocol.MinimumInterval - (clock.GetUtcNow() - previous);
                if (savedTimestamp is { } timestamp)
                {
                    var monotonicDelay = CheckpointProtocol.MinimumInterval - clock.GetElapsedTime(timestamp);
                    if (monotonicDelay > delay) delay = monotonicDelay;
                }
                if (delay <= TimeSpan.Zero) break;
                await Task.Delay(delay, clock, stop.Token);
            }
            stop.Token.ThrowIfCancellationRequested();
            var at = clock.GetUtcNow();
            at = new(at.UtcTicks / 10 * 10, TimeSpan.Zero);
            // An interrupted save may already be committed. Never reuse its sequence with different state.
            sequence = checked(sequence + 1);
            savedAt = at;
            savedTimestamp = clock.GetTimestamp();
            await persist(payload, sequence, at, stop.Token);
            Volatile.Write(ref state, payload);
        }
        finally { gate.Release(); }
    }

    private static JsonTypeInfo<T> TypeInfo<T>(JsonTypeInfo<T>? supplied) =>
        supplied ?? OutputJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
        throw new NotSupportedException($"Checkpoint state requires source-generated JSON type information for {typeof(T).Name}.");

    private sealed class BoundedState : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckSize(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckSize(buffer.Length);
            base.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        private void CheckSize(int count)
        {
            if (Length + count > CheckpointProtocol.MaximumBytes)
                throw new ArgumentException("Checkpoint JSON exceeds the 1 MiB limit.");
        }
    }
}
