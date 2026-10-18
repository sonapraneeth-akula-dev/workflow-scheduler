using Wf.Contracts.Definition;

namespace Wf.Agent.Secrets;

public interface ISecretStore
{
    ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default);
}

public sealed class SecretResolutionException : Exception
{
    public SecretResolutionException(SecretReference reference)
        : base($"Secret '{reference.Name}' is missing or unavailable.") { }

    public SecretResolutionException() : base("Task secret references are invalid or unavailable.") { }
}
