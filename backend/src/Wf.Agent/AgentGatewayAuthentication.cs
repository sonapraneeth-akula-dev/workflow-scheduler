using Azure.Core;
using Azure.Identity;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace Wf.Agent;

public sealed class AgentGatewayAuthentication
{
    private readonly TokenCredential? credential;
    private readonly string? scope;

    private AgentGatewayAuthentication(string? scope = null, TokenCredential? credential = null)
    {
        this.scope = scope;
        this.credential = credential;
    }

    public static AgentGatewayAuthentication FromConfiguration(IConfiguration configuration, AgentOptions options,
        TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        var section = configuration.GetSection("Agent:Authentication");
        if (section.Value is not null || section.GetChildren().Any(child =>
            !new[] { "Mode", "Scope", "ManagedIdentityClientId" }.Contains(child.Key, StringComparer.OrdinalIgnoreCase) ||
            child.GetChildren().Any()))
            throw new ArgumentException("Unrecognized Agent:Authentication configuration key.");
        var mode = section["Mode"] ?? "None";
        var scope = section["Scope"];
        var client = section["ManagedIdentityClientId"];
        if (mode == "None")
        {
            if (scope is not null || client is not null || credential is not null)
                throw new ArgumentException("Unauthenticated agent transport cannot include managed identity configuration.");
            return new();
        }
        if (mode != "ManagedIdentity")
            throw new ArgumentException("Agent:Authentication:Mode must be None or ManagedIdentity.");
        if (options.Engines.Any(endpoint => endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Managed identity agent authentication requires HTTPS for every engine.");
        if (scope is null || scope.Length != 51 || !scope.StartsWith("api://", StringComparison.Ordinal) ||
            !scope.EndsWith("/.default", StringComparison.Ordinal) ||
            !Guid.TryParseExact(scope[6..^9], "D", out var audience) || audience == Guid.Empty ||
            scope != $"api://{audience:D}/.default")
            throw new ArgumentException("Agent:Authentication:Scope must be api://<API client UUID>/.default.");
        if (client is not null && (!Guid.TryParseExact(client, "D", out var id) || id == Guid.Empty))
            throw new ArgumentException("Agent:Authentication:ManagedIdentityClientId must be a non-empty client UUID.");
        return new(scope, credential ?? new ManagedIdentityCredential(client is null
            ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(client)));
    }

    internal async Task<Metadata?> CreateHeadersAsync(Uri endpoint, CancellationToken token)
    {
        if (credential is null) return null;
        if (!AgentOptions.IsEngineOrigin(endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Agent bearer credentials require an HTTPS engine origin.", nameof(endpoint));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        AccessToken access;
        try { access = await credential.GetTokenAsync(new TokenRequestContext([scope!]), timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new AuthenticationFailedException("Agent managed identity acquisition timed out."); }
        if (access.ExpiresOn <= DateTimeOffset.UtcNow || string.IsNullOrEmpty(access.Token) ||
            access.Token.Any(character => character <= ' ' || character >= '\x7f'))
            throw new AuthenticationFailedException("Managed identity returned an expired or invalid agent access token.");
        return new Metadata { { "authorization", "Bearer " + access.Token } };
    }
}
