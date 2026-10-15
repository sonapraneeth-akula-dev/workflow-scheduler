namespace Wf.Sdk;

public sealed class TaskContext(CancellationToken cancellationToken = default)
{
    public CancellationToken CancellationToken { get; } = cancellationToken;
}
