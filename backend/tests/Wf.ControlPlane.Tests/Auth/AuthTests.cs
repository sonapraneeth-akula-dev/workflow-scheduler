using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Tests.Auth;

public sealed class AuthTests
{
    private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
    private const string ClientId = "562b0870-4522-43da-8587-41e9b788f309";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Ppe")]
    public void Development_handler_is_rejected_outside_Development(string environment)
    {
        var builder = CreateBuilder(environment, "Development");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment));
        Assert.Contains("Development environment", exception.Message);
    }

    [Theory]
    [InlineData("AzureAd:TenantId", null)]
    [InlineData("AzureAd:TenantId", "common")]
    [InlineData("AzureAd:TenantId", "organizations")]
    [InlineData("AzureAd:TenantId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("AzureAd:ClientId", null)]
    [InlineData("AzureAd:ClientId", "not-an-application-id")]
    [InlineData("AzureAd:Instance", "http://login.microsoftonline.com/")]
    [InlineData("AzureAd:Instance", "https://user:secret@login.microsoftonline.com/")]
    [InlineData("AzureAd:Instance", "https://login.microsoftonline.com/path")]
    [InlineData("AzureAd:Instance", "https://login.microsoftonline.com/?tenant=other")]
    [InlineData("Authentication:Mode", "Disabled")]
    public void Invalid_configuration_fails_at_registration(string key, string? value)
    {
        var builder = CreateBuilder("Production", "Entra");
        builder.Configuration[key] = value;
        Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment));
    }

    [Fact]
    public void Development_environment_does_not_implicitly_enable_bypass()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment));
    }

    [Fact]
    public async Task Unauthenticated_API_returns_401_but_explicit_health_remains_public()
    {
        await using var server = await Server.Start();
        using var response = await server.Client.GetAsync("/private", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/healthz", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("Admin", "/admin")]
    [InlineData("Registrar", "/registrar")]
    [InlineData("Agent", "/agent")]
    public async Task Valid_signed_app_role_token_is_accepted(string role, string path)
    {
        await using var server = await Server.Start();
        using var response = await server.Send(path, server.Token(role));
        Assert.True(response.StatusCode == HttpStatusCode.OK, server.AuthenticationFailure?.ToString() ?? response.StatusCode.ToString());
        Assert.Equal("caller", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Admin", "/agent")]
    [InlineData("Admin", "/registrar")]
    [InlineData("Agent", "/admin")]
    [InlineData("Agent", "/registrar")]
    [InlineData("Registrar", "/admin")]
    [InlineData("Registrar", "/agent")]
    [InlineData("agent", "/agent")]
    [InlineData("", "/agent")]
    public async Task Valid_identity_without_required_role_receives_403(string role, string path)
    {
        await using var server = await Server.Start();
        using var response = await server.Send(path, server.Token(role, scope: role.Length == 0 ? "access_as_user" : null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Agent", "/agent", "user", null)]
    [InlineData("Agent", "/agent", null, "access_as_user")]
    [InlineData("Agent", "/agent", "app", "access_as_user")]
    [InlineData("Agent", "/agent", null, null)]
    [InlineData("Registrar", "/registrar", "user", "access_as_user")]
    public async Task Machine_endpoint_rejects_user_and_ambiguous_tokens(string role, string path, string? identityType, string? scope)
    {
        await using var server = await Server.Start();
        using var response = await server.Send(path, server.Token(role, identityType: identityType, scope: scope));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-tenant")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-signature")]
    [InlineData("unsigned")]
    [InlineData("malformed")]
    [InlineData("no-scopes-or-roles")]
    public async Task Untrusted_JWT_is_rejected(string fault)
    {
        await using var server = await Server.Start();
        using var otherKey = RSA.Create(2048);
        var token = fault switch
        {
            "wrong-audience" => server.Token("Admin", audience: Guid.NewGuid().ToString()),
            "wrong-issuer" => server.Token("Admin", issuer: "https://attacker.invalid/v2.0"),
            "wrong-tenant" => server.Token("Admin", tenant: Guid.NewGuid().ToString()),
            "expired" => server.Token("Admin", expires: DateTime.UtcNow.AddMinutes(-1)),
            "future" => server.Token("Admin", notBefore: DateTime.UtcNow.AddMinutes(10)),
            "wrong-signature" => server.Token("Admin", key: new RsaSecurityKey(otherKey) { KeyId = "other" }),
            "unsigned" => server.Token("Admin", unsigned: true),
            "malformed" => "not-a-jwt",
            "no-scopes-or-roles" => server.Token(""),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        using var response = await server.Send("/admin", token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_delegated_Admin_token_is_permitted_only_on_user_compatible_endpoints()
    {
        await using var server = await Server.Start();
        using var response = await server.Send("/admin", server.Token("Admin", identityType: "user", scope: "access_as_user"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Explicit_development_auth_uses_fixed_local_identity_not_request_headers()
    {
        await using var server = await Server.Start(development: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/agent");
        request.Headers.Add("X-User", "attacker");
        request.Headers.Add("X-Roles", "Viewer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Local development", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static WebApplicationBuilder CreateBuilder(string environment, string mode)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = mode, ["AzureAd:TenantId"] = Tenant, ["AzureAd:ClientId"] = ClientId
        });
        return builder;
    }

    private sealed class Server(WebApplication app, HttpClient client, RSA rsa, RsaSecurityKey signingKey,
        Func<Exception?> authenticationFailure) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public Exception? AuthenticationFailure => authenticationFailure();

        public static async Task<Server> Start(bool development = false)
        {
            var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "auth-test" };
            var builder = CreateBuilder(development ? "Development" : "Production", development ? "Development" : "Entra");
            Exception? authenticationFailure = null;
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            if (!development)
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                metadata.SigningKeys.Add(key);
                var configurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Configuration = metadata;
                    options.ConfigurationManager = configurationManager;
                    options.TokenValidationParameters.IssuerValidator =
                        AadIssuerValidator.GetAadIssuerValidator(Issuer, null, _ => configurationManager).Validate;
                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                    options.Events.OnAuthenticationFailed = context =>
                    {
                        authenticationFailure = context.Exception;
                        return Task.CompletedTask;
                    };
                });
            }
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/private", (ClaimsPrincipal caller) => caller.Identity!.Name);
            app.MapGet("/admin", (ClaimsPrincipal caller) => caller.Identity!.Name).RequireAuthorization(AuthExtensions.Admin);
            app.MapGet("/registrar", (ClaimsPrincipal caller) => caller.Identity!.Name).RequireAuthorization(AuthExtensions.Registrar);
            app.MapGet("/agent", (ClaimsPrincipal caller) => caller.Identity!.Name).RequireAuthorization(AuthExtensions.Agent);
            app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous();
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Server(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) },
                rsa, key, () => authenticationFailure);
        }

        public Task<HttpResponseMessage> Send(string path, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return SendCore(request);
        }

        private async Task<HttpResponseMessage> SendCore(HttpRequestMessage request)
        {
            using (request) return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public string Token(string role, string audience = ClientId, string issuer = Issuer, string tenant = Tenant,
            string? identityType = "app", string? scope = null, DateTime? expires = null, DateTime? notBefore = null,
            RsaSecurityKey? key = null, bool unsigned = false)
        {
            var claims = new List<Claim>
            {
                new("tid", tenant), new("ver", "2.0"), new("name", "caller"),
                new("oid", "44cd65e3-f210-4201-b5a8-74321260704a"),
                new("azp", "60d24b19-f65a-4ed4-96cc-9174bfcacabc")
            };
            if (role.Length != 0) claims.Add(new Claim("roles", role));
            if (identityType is not null) claims.Add(new Claim("idtyp", identityType));
            if (scope is not null) claims.Add(new Claim("scp", scope));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
                notBefore ?? DateTime.UtcNow.AddMinutes(-10), expires ?? DateTime.UtcNow.AddMinutes(20),
                unsigned ? null : new SigningCredentials(key ?? signingKey, SecurityAlgorithms.RsaSha256)));
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            rsa.Dispose();
        }
    }
}
