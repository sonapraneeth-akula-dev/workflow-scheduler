using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Live;

namespace Wf.Testing.RunQueries;

public sealed class RunQueryServer(WebApplication app, HttpClient client, RSA rsa, RsaSecurityKey key,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
    private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    public static async Task<RunQueryServer> Start(NpgsqlDataSource source, CancellationToken token, bool abort = false, bool live = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "run-query-test" };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["AzureAd:TenantId"] = Tenant;
        builder.Configuration["AzureAd:ClientId"] = Audience;
        builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(FailureCatalog.Load(Path.Combine(AppContext.BaseDirectory, "failure-categories")));
        builder.Services.AddTeamAuthorization();
        builder.Services.AddWorkflowOpenApi();
        if (abort || live) builder.Services.AddSingleton(TimeProvider.System);
        if (live) builder.Services.AddWorkflowLive();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunQueryJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunHistoryJsonContext.Default);
            if (abort) options.SerializerOptions.TypeInfoResolverChain.Insert(0, AbortRunJson.Default);
        });
        var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
        metadata.SigningKeys.Add(key);
        var manager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Configuration = metadata;
            options.ConfigurationManager = manager;
            options.TokenValidationParameters.IssuerValidator =
                AadIssuerValidator.GetAadIssuerValidator(Issuer, null, _ => manager).Validate;
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
        });
        var app = builder.Build();
        try
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRunQueryEndpoints();
            app.MapRunHistoryEndpoints();
            if (abort) app.MapRunAbortEndpoint();
            if (live) app.MapHub<LiveHub>(LiveHub.Path);
            app.MapWorkflowOpenApi();
            await app.StartAsync(token);
            if (live)
            {
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
                ready.CancelAfter(TimeSpan.FromSeconds(10));
                while (!app.Services.GetRequiredService<LiveFanout>().IsReady) await Task.Delay(10, ready.Token);
            }
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) }, rsa, key, token);
        }
        catch
        {
            await app.DisposeAsync();
            rsa.Dispose();
            throw;
        }
    }

    public string Jwt(Guid? group = null, string? identity = "user", string? scope = "api.read",
        string role = "", Claim[]? extra = null)
    {
        var claims = new List<Claim> { new("tid", Tenant), new("ver", "2.0"), new("oid", Guid.NewGuid().ToString()),
            new("azp", "60d24b19-f65a-4ed4-96cc-9174bfcacabc") };
        if (group is not null) claims.Add(new("groups", group.ToString()!));
        if (identity is not null) claims.Add(new("idtyp", identity));
        if (scope is not null) claims.Add(new("scp", scope));
        if (role.Length > 0) claims.Add(new("roles", role));
        if (extra is not null) claims.AddRange(extra);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
    }

    public Uri Origin => client.BaseAddress!;

    public async Task<HttpResponseMessage> Get(string path, string? jwt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (jwt is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await client.SendAsync(request, cancellationToken);
    }

    public async Task<HttpResponseMessage> Post(string path, string? jwt, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (jwt is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await client.SendAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.StopAsync(timeout.Token);
        }
        finally
        {
            try { await app.DisposeAsync(); }
            finally { rsa.Dispose(); }
        }
    }
}
