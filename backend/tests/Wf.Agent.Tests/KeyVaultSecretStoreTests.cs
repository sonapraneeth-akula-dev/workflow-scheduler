using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Wf.Agent.Secrets;
using Wf.Contracts.Definition;

namespace Wf.Agent.Tests;

public sealed class KeyVaultSecretStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string ClientId = "15d8fd45-e1b0-4ff2-82f0-c77997d91b2c";

    [Theory]
    [InlineData(null)]
    [InlineData(ClientId)]
    public async Task Official_SDK_uses_only_selected_managed_identity_and_gets_one_secret_without_listing(string? clientId)
    {
        var identityRequests = new List<Uri>();
        var vaultRequests = new List<(Uri Uri, bool Authenticated)>();
        using var identityTransport = new HttpClientTransport(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            identityRequests.Add(request.RequestUri!);
            return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                access_token = "controlled-managed-identity-token",
                expires_on = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                token_type = "Bearer", resource = "https://vault.azure.net"
            })));
        }));
        using var vaultTransport = new HttpClientTransport(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            var authenticated = request.Headers.Authorization?.Parameter == "controlled-managed-identity-token";
            vaultRequests.Add((request.RequestUri!, authenticated));
            if (!authenticated)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    """Bearer authorization="https://login.windows.net/15d8fd45-e1b0-4ff2-82f0-c77997d91b2c", resource="https://vault.azure.net" """);
                return Task.FromResult(challenge);
            }
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"value":"resolved-🔑-value","id":"https://test.vault.azure.net/secrets/token/version","attributes":{"enabled":true}}"""));
        }));
        var factories = 0;
        var store = new KeyVaultSecretStore(new Dictionary<string, KeyVaultStoreOptions>
        { ["approved"] = new("https://test.vault.azure.net", clientId) }, (uri, credential, options) =>
        {
            factories++;
            Assert.IsType<ManagedIdentityCredential>(credential);
            Assert.Equal(2, options.Retry.MaxRetries);
            Assert.Equal(TimeSpan.FromSeconds(5), options.Retry.NetworkTimeout);
            Assert.False(options.Diagnostics.IsLoggingEnabled);
            Assert.False(options.Diagnostics.IsLoggingContentEnabled);
            options.Transport = vaultTransport;
            return new SecretClient(uri, credential, options);
        }, options =>
        {
            Assert.False(options.Diagnostics.IsLoggingEnabled);
            Assert.False(options.IsUnsafeSupportLoggingEnabled);
            options.Transport = identityTransport;
        });
        Assert.Equal("resolved-🔑-value", await store.ResolveAsync(new("secret://approved/token"), Token));
        Assert.Equal(1, factories);
        Assert.NotEmpty(identityRequests);
        Assert.All(vaultRequests, request => Assert.Equal("/secrets/token/", request.Uri.AbsolutePath));
        Assert.Contains(vaultRequests, request => request.Authenticated);
        Assert.All(identityRequests, uri =>
        {
            Assert.Contains("resource=https://vault.azure.net", Uri.UnescapeDataString(uri.Query), StringComparison.OrdinalIgnoreCase);
            if (clientId is not null) Assert.Contains("client_id=" + clientId, uri.Query);
            else Assert.DoesNotContain("client_id=", uri.Query);
        });
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task SDK_failure_response_and_credentials_never_enter_error_or_inner_exception(int status)
    {
        var count = 0;
        using var transport = new HttpClientTransport(new Handler((_, _) =>
        {
            count++;
            return Task.FromResult(Json((HttpStatusCode)status,
                """{"error":{"code":"SecretNotFound","message":"sensitive-cloud-response-credentials"}}"""));
        }));
        var store = Store(transport);
        var error = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await store.ResolveAsync(new("secret://approved/token"), Token));
        Assert.Equal("Secret 'secret://approved/token' is missing or unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.Equal(status is 429 or 500 ? 3 : 1, count);
    }

    [Fact]
    public async Task SDK_authentication_and_cancellation_fail_safely_without_fallback()
    {
        using var transport = new HttpClientTransport(new Handler((_, _) =>
            throw new AuthenticationFailedException("sensitive-identity-response")));
        var error = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await Store(transport).ResolveAsync(new("secret://approved/token"), Token));
        Assert.DoesNotContain("sensitive", error.ToString());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Store(transport).ResolveAsync(new("secret://approved/token"), cancelled.Token));
        using var waiting = new HttpClientTransport(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Store(waiting).ResolveAsync(new("secret://approved/token"), stop.Token));
    }

    [Theory]
    [InlineData("sensitive-invalid-response")]
    [InlineData("""{"value":123,"attributes":{}}""")]
    [InlineData("""{"value":"","attributes":{}}""")]
    public async Task Malformed_or_empty_SDK_response_fails_with_only_the_safe_reference(string body)
    {
        using var transport = new HttpClientTransport(new Handler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body))));
        var error = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await Store(transport).ResolveAsync(new("secret://approved/token"), Token));
        Assert.Equal("Secret 'secret://approved/token' is missing or unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Fact]
    public async Task Actual_managed_identity_SDK_failure_drops_identity_response_and_has_no_developer_fallback()
    {
        var identityRequests = 0;
        using var identity = new HttpClientTransport(new Handler((_, _) =>
        {
            identityRequests++;
            return Task.FromResult(Json(HttpStatusCode.BadRequest,
                """{"error":"invalid_request","error_description":"sensitive-identity-response"}"""));
        }));
        using var vault = new HttpClientTransport(new Handler((_, _) =>
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.TryAddWithoutValidation("WWW-Authenticate",
                """Bearer authorization="https://login.windows.net/15d8fd45-e1b0-4ff2-82f0-c77997d91b2c", resource="https://vault.azure.net" """);
            return Task.FromResult(challenge);
        }));
        // A unique identity avoids reusing the SDK's process-wide successful token cache.
        var store = new KeyVaultSecretStore(new Dictionary<string, KeyVaultStoreOptions>
        { ["approved"] = new("https://test.vault.azure.net", Guid.NewGuid().ToString("D")) },
            (uri, credential, options) =>
            {
                Assert.IsType<ManagedIdentityCredential>(credential);
                options.Transport = vault;
                return new SecretClient(uri, credential, options);
            }, options => options.Transport = identity);
        var error = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await store.ResolveAsync(new("secret://approved/token"), Token));
        Assert.InRange(identityRequests, 1, 3);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("secret://unknown/token")]
    [InlineData("token")]
    [InlineData("secret://approved/path/token")]
    [InlineData("secret://approved/token.name")]
    [InlineData("secret://approved/token_name")]
    public async Task Unknown_alias_or_unsupported_vault_name_never_makes_a_request(string reference)
    {
        using var transport = new HttpClientTransport(new Handler((_, _) => throw new InvalidOperationException("Must not request.")));
        await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await Store(transport).ResolveAsync(new(reference), Token));
    }

    [Theory]
    [InlineData("http://test.vault.azure.net", null)]
    [InlineData("https://test.vault.azure.net/path", null)]
    [InlineData("https://test.vault.azure.net?token=x", null)]
    [InlineData("https://test.vault.azure.net#fragment", null)]
    [InlineData("https://user:password@test.vault.azure.net", null)]
    [InlineData("https://127.0.0.1", null)]
    [InlineData("https://localhost", null)]
    [InlineData("https://test.vault.azure.net:444", null)]
    [InlineData("https://test.vault.azure.net", "invalid")]
    public void Invalid_config_fails_before_any_task_or_cloud_request(string uri, string? clientId)
    {
        var error = Assert.Throws<ArgumentException>(() => new KeyVaultSecretStore(new Dictionary<string, KeyVaultStoreOptions>
        { ["approved"] = new(uri, clientId) }));
        Assert.DoesNotContain(uri, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("Typo", "Secrets:Provider")]
    [InlineData("https://test.vault.azure.net", "Secrets:VaultUri")]
    [InlineData("https://test.vault.azure.net", "Secrets:Vaults:approved:VaultURItypo")]
    public void Unknown_provider_or_config_is_explicit(string value, string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Secrets:Provider"] = "KeyVault", [key] = value }).Build();
        Assert.Throws<ArgumentException>(() => SecretStoreConfiguration.Create(configuration));
    }

    [Theory]
    [InlineData("Secrets")]
    [InlineData("Secrets:Provider:nested")]
    [InlineData("Secrets:Vaults")]
    [InlineData("Secrets:LocalDirectory:nested")]
    [InlineData("Secrets:Vaults:approved:ManagedIdentityClientId:nested")]
    public void Malformed_config_shapes_never_silently_select_another_provider_or_identity(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:Provider"] = "KeyVault",
            ["Secrets:Vaults:approved:VaultUri"] = "https://test.vault.azure.net",
            [key] = "invalid-configuration-value"
        }).Build();
        var error = Assert.Throws<ArgumentException>(() => SecretStoreConfiguration.Create(configuration));
        Assert.DoesNotContain("invalid-configuration-value", error.ToString());
    }

    private static KeyVaultSecretStore Store(HttpPipelineTransport transport) =>
        new(new Dictionary<string, KeyVaultStoreOptions> { ["approved"] = new("https://test.vault.azure.net") },
            (uri, _, options) =>
            {
                options.Transport = transport;
                return new SecretClient(uri, new NoNetworkCredential(), options);
            });

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("controlled-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
