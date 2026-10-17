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
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Live;

namespace Wf.Integration.Tests.Execution;

internal sealed class MetricsApiServer(WebApplication app, HttpClient client, RSA rsa, RsaSecurityKey key) : IAsyncDisposable
{
    private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
    private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    public static async Task<MetricsApiServer> Start(NpgsqlDataSource source, CancellationToken token, bool browser = false,
        TimeProvider? timeProvider = null)
    {
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "run-metrics-test" };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["AzureAd:TenantId"] = Tenant;
        builder.Configuration["AzureAd:ClientId"] = Audience;
        builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(source);
        builder.Services.AddTeamAuthorization();
        builder.Services.AddWorkflowOpenApi();
        builder.Services.AddSingleton<RunStore>();
        builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
        if (browser)
        {
            builder.Services.AddWorkflowLive();
        }
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, MetricsJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunTimelineJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.ControlPlane.Api.Series.SeriesTrendsJson.Default);
            if (browser)
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunEventsJsonContext.Default);
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, VersionsJsonContext.Default);
            }
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
            app.MapMetricsEndpoints();
            app.MapRunTimelineEndpoints();
            Wf.ControlPlane.Api.Series.TrendsEndpoint.MapTrendsEndpoint(app);
            if (browser)
            {
                app.MapRunEventsEndpoints();
                app.MapVersionsEndpoints();
                app.MapPermissionsEndpoints();
                app.MapHub<LiveHub>(LiveHub.Path, options => options.CloseOnAuthenticationExpiration = true);
            }
            app.MapWorkflowOpenApi();
            await app.StartAsync(token);
            if (browser)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (!app.Services.GetRequiredService<LiveFanout>().IsReady)
                    await Task.Delay(10, deadline.Token);
            }
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) }, rsa, key);
        }
        catch
        {
            await app.DisposeAsync();
            rsa.Dispose();
            throw;
        }
    }

    public string Api => client.BaseAddress!.ToString().TrimEnd('/');

    public string Jwt(Guid? group = null, string role = "", string identity = "user")
    {
        var claims = new List<Claim> { new("tid", Tenant), new("ver", "2.0"), new("oid", Guid.NewGuid().ToString()),
            new("idtyp", identity), new("scp", "api.read"), new("azp", "60d24b19-f65a-4ed4-96cc-9174bfcacabc") };
        if (group is not null) claims.Add(new("groups", group.ToString()!));
        if (role.Length > 0) claims.Add(new("roles", role));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
    }

    public async Task<HttpResponseMessage> Get(string path, string? jwt, CancellationToken token)
    {
        var uri = new Uri(client.BaseAddress!.ToString().TrimEnd('/') + path,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (jwt is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await client.SendAsync(request, token);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        try { await app.DisposeAsync(); }
        finally { rsa.Dispose(); }
    }
}
