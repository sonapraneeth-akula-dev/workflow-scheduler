using System.Collections.Immutable;
using Wf.Contracts;

namespace Wf.Sdk;

public sealed record ReportPublication(
    string SourceKey, string Kind, string Title, ReadOnlyMemory<byte> Payload, ImmutableArray<string> Tags);

public sealed class ReportPublisher : IReports
{
    private readonly Func<ReportPublication, CancellationToken, Task> publish;
    private readonly Quota quota;
    private readonly string? sourceKey;

    public ReportPublisher(Func<ReportPublication, CancellationToken, Task> publish) : this(publish, ReportLimits.Default) { }

    public ReportPublisher(Func<ReportPublication, CancellationToken, Task> publish, ReportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(limits);
        this.publish = publish;
        quota = new(limits);
    }

    private ReportPublisher(ReportPublisher publisher, string sourceKey)
    {
        publish = publisher.publish;
        quota = publisher.quota;
        this.sourceKey = sourceKey;
    }

    internal ReportPublisher ForSource(string key) => new(this, key);
    internal ReportPublisher ForRun() => new(publish, quota.Limits);

    public async Task PublishAsync(string kind, string title, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken, IReadOnlyList<string>? tags = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (sourceKey is null)
            throw new InvalidOperationException("Reports must be published through an executing task context.");
        var copiedTags = tags?.ToImmutableArray() ?? [];
        foreach (var tag in copiedTags)
            ArgumentException.ThrowIfNullOrWhiteSpace(tag, nameof(tags));
        quota.Reserve(payload.Length);
        try
        {
            await publish(new(sourceKey, kind, title, payload.ToArray(), copiedTags), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            quota.Release();
            throw;
        }
    }

    private sealed class Quota(ReportLimits limits)
    {
        public ReportLimits Limits { get; } = limits;
        private int reserved;

        public void Reserve(int bytes)
        {
            if (bytes > Limits.MaxBytes)
                throw new ReportLimitExceededException($"Report size {bytes} bytes exceeds the per-report limit of {Limits.MaxBytes} bytes.");
            int current;
            do
            {
                current = Volatile.Read(ref reserved);
                if (current >= Limits.MaxCount)
                    throw new ReportLimitExceededException($"Run report count would exceed the limit of {Limits.MaxCount} reports.");
            } while (Interlocked.CompareExchange(ref reserved, current + 1, current) != current);
        }

        public void Release() => Interlocked.Decrement(ref reserved);
    }
}
