using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Wf.Contracts.Definition;

namespace Wf.Agent.Secrets;

public sealed class KeyVaultSecretStore : ISecretStore
{
    private readonly IReadOnlyDictionary<string, SecretClient> clients;

    public KeyVaultSecretStore(IReadOnlyDictionary<string, KeyVaultStoreOptions> stores)
        : this(stores, null) { }

    internal KeyVaultSecretStore(IReadOnlyDictionary<string, KeyVaultStoreOptions> stores,
        Func<Uri, ManagedIdentityCredential, SecretClientOptions, SecretClient>? createClient,
        Action<ManagedIdentityCredentialOptions>? configureCredential = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Count == 0) throw new ArgumentException("Secrets:Vaults requires at least one approved vault alias.");
        var result = new Dictionary<string, SecretClient>(StringComparer.Ordinal);
        foreach (var (alias, options) in stores)
        {
            if (!SecretReference.IsValidName($"secret://{alias}/name") || alias == "local" ||
                !Uri.TryCreate(options.VaultUri, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                options.ManagedIdentityClientId is { } id && (!Guid.TryParseExact(id, "D", out var guid) || guid == Guid.Empty))
                throw new ArgumentException("Secrets:Vaults requires safe aliases, HTTPS vault origins and valid managed identity client IDs.");
            var identity = options.ManagedIdentityClientId is { } clientId
                ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
            var credentialOptions = new ManagedIdentityCredentialOptions(identity);
            credentialOptions.Retry.MaxRetries = 2;
            credentialOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            credentialOptions.Diagnostics.IsLoggingEnabled = false;
            credentialOptions.Diagnostics.IsLoggingContentEnabled = false;
            credentialOptions.Diagnostics.IsDistributedTracingEnabled = false;
            configureCredential?.Invoke(credentialOptions);
            var credential = new ManagedIdentityCredential(credentialOptions);
            var clientOptions = new SecretClientOptions
            {
                Retry = { MaxRetries = 2, Delay = TimeSpan.FromMilliseconds(200),
                    MaxDelay = TimeSpan.FromSeconds(2), NetworkTimeout = TimeSpan.FromSeconds(5) },
                Diagnostics = { IsLoggingContentEnabled = false, IsLoggingEnabled = false, IsDistributedTracingEnabled = false }
            };
            result.Add(alias, createClient is null ? new SecretClient(uri, credential, clientOptions) :
                createClient(uri, credential, clientOptions));
        }
        clients = result;
    }

    public async ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!clients.TryGetValue(reference.Store, out var client) ||
            reference.SecretName.Length > 127 || reference.SecretName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new SecretResolutionException(reference);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var response = await client.GetSecretAsync(reference.SecretName, cancellationToken: timeout.Token);
            var value = response.Value.Value;
            if (string.IsNullOrEmpty(value) || value.Contains('\0') ||
                System.Text.Encoding.UTF8.GetByteCount(value) > LocalSecretStore.MaxSecretBytes)
                throw new SecretResolutionException(reference);
            return value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SecretResolutionException(reference); }
        catch (Exception error) when (error is RequestFailedException or AuthenticationFailedException or HttpRequestException or
            System.Text.Json.JsonException or FormatException or InvalidOperationException)
        { throw new SecretResolutionException(reference); }
    }
}

public sealed record KeyVaultStoreOptions(string VaultUri, string? ManagedIdentityClientId = null);
