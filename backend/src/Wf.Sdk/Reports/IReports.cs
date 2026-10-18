namespace Wf.Sdk;

public interface IReports
{
    Task PublishAsync(string kind, string title, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken, IReadOnlyList<string>? tags = null);
}
