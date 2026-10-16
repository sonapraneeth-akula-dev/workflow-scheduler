using System.Collections.Immutable;

namespace Wf.Sdk;

public sealed record ReportPublication(
    string SourceKey, string Kind, string Title, ReadOnlyMemory<byte> Payload, ImmutableArray<string> Tags);

public sealed class ReportPublisher : IReports
{
    private readonly Func<ReportPublication, CancellationToken, Task> publish;
    private readonly string? sourceKey;

    public ReportPublisher(Func<ReportPublication, CancellationToken, Task> publish) : this(publish, null) { }

    private ReportPublisher(Func<ReportPublication, CancellationToken, Task> publish, string? sourceKey)
    {
        ArgumentNullException.ThrowIfNull(publish);
        this.publish = publish;
        this.sourceKey = sourceKey;
    }

    internal ReportPublisher ForSource(string key) => new(publish, key);

    public Task PublishAsync(string kind, string title, ReadOnlyMemory<byte> payload,
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
        return publish(new(sourceKey, kind, title, payload.ToArray(), copiedTags), cancellationToken);
    }
}
