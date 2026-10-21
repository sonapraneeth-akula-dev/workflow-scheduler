using System.Net;
using System.Net.Sockets;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class AgentGatewayAuthenticationTests
{
    private const string Scope = "api://a7e3e1d9-77e4-4c40-af04-9189da15d8cd/.default";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Default_preserves_local_transport_without_an_authorization_header()
    {
        var authentication = AgentGatewayAuthentication.FromConfiguration(Config(), Options("http://localhost:5001"));
        Assert.Null(await authentication.CreateHeadersAsync(new Uri("http://localhost:5001"), Token));
    }

    [Fact]
    public async Task Each_connection_uses_the_reusable_credential_for_the_API_scope_and_current_token()
    {
        var calls = 0;
        var credential = new Credential((request, token) =>
        {
            Assert.Equal([Scope], request.Scopes);
            Assert.True(token.CanBeCanceled);
            return ValueTask.FromResult(new AccessToken("test-token-" + ++calls, DateTimeOffset.UtcNow.AddMinutes(30)));
        });
        var authentication = Authentication(credential);
        var first = await authentication.CreateHeadersAsync(new Uri("https://controller-0:8444"), Token);
        var next = await authentication.CreateHeadersAsync(new Uri("https://controller-1:8444"), Token);
        Assert.Equal("Bearer test-token-1", Assert.Single(first!).Value);
        Assert.Equal("authorization", first![0].Key);
        Assert.Equal("Bearer test-token-2", Assert.Single(next!).Value);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("http://localhost:5001")]
    [InlineData("http://controller:8444")]
    public void Managed_identity_rejects_even_explicitly_opted_in_plaintext_engines(string endpoint) =>
        Assert.Throws<ArgumentException>(() => AgentGatewayAuthentication.FromConfiguration(Config(
            ("Mode", "ManagedIdentity"), ("Scope", Scope)), Options(endpoint, insecure: true)));

    [Theory]
    [InlineData("Mode", "DefaultAzureCredential")]
    [InlineData("Scope", "https://management.azure.com/.default")]
    [InlineData("Scope", "api:///.default")]
    [InlineData("Scope", "api://00000000-0000-0000-0000-000000000000/.default")]
    [InlineData("Scope", "api://a7e3e1d9-77e4-4c40-af04-9189da15d8cd/.default other")]
    [InlineData("ManagedIdentityClientId", "not-a-uuid")]
    [InlineData("ManagedIdentityClientId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("ClientSecret", "not-supported")]
    public void Invalid_or_unrecognized_identity_configuration_fails_closed(string key, string value)
    {
        var values = new Dictionary<string, string?> { ["Mode"] = "ManagedIdentity", ["Scope"] = Scope, [key] = value };
        Assert.Throws<ArgumentException>(() => AgentGatewayAuthentication.FromConfiguration(
            Config(values.Select(item => (item.Key, item.Value!)).ToArray()), Options("https://controller")));
    }

    [Fact]
    public void None_cannot_silently_ignore_managed_identity_settings() =>
        Assert.Throws<ArgumentException>(() => AgentGatewayAuthentication.FromConfiguration(
            Config(("Mode", "None"), ("Scope", Scope)), Options("https://controller")));

    [Fact]
    public async Task An_untrusted_redirect_cannot_send_bearer_credentials_over_plaintext()
    {
        var credential = new Credential((_, _) => throw new InvalidOperationException("Credential must not be called."));
        await Assert.ThrowsAsync<ArgumentException>(() => Authentication(credential)
            .CreateHeadersAsync(new Uri("http://localhost"), Token));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("token\r\nother", false)]
    [InlineData("token", true)]
    public async Task Invalid_or_expired_tokens_never_become_headers(string token, bool expired)
    {
        var credential = new Credential((_, _) => ValueTask.FromResult(new AccessToken(token,
            DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 30))));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => Authentication(credential)
            .CreateHeadersAsync(new Uri("https://controller"), Token));
    }

    [Fact]
    public async Task Credential_failure_retries_without_opening_an_anonymous_connection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var authentication = Authentication(new Credential((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2) retried.TrySetResult();
            throw new CredentialUnavailableException("Fixture identity endpoint unavailable.");
        }));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var outbox = new AgentOutbox();
        using var connection = new AgentConnection(Options(endpoint), new AgentTelemetry(TimeProvider.System),
            new NoCommands(), TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox, authentication);
        var running = connection.RunAsync(stop.Token);
        try
        {
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(listener.Pending());
            Assert.False(running.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task Shutdown_cancels_identity_acquisition_without_retrying_or_falling_back()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authentication = Authentication(new Credential(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = authentication.CreateHeadersAsync(new Uri("https://controller"), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static AgentGatewayAuthentication Authentication(TokenCredential credential) =>
        AgentGatewayAuthentication.FromConfiguration(Config(("Mode", "ManagedIdentity"), ("Scope", Scope)),
            Options("https://controller-0:8444"), credential);

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(
            item => "Agent:Authentication:" + item.Key, item => (string?)item.Value)).Build();

    private static AgentOptions Options(string endpoint, bool insecure = false) =>
        new([new Uri(endpoint)], Guid.NewGuid().ToString("D"), "identity-test", "tests", 1, allowInsecureTransport: insecure);

    private sealed class Credential(Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> get) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Agent authentication must use asynchronous credential acquisition.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            get(requestContext, cancellationToken);
    }

    private sealed class NoCommands : IAgentCommandHandler
    {
        public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No commands are expected without an authenticated connection.");
    }
}
