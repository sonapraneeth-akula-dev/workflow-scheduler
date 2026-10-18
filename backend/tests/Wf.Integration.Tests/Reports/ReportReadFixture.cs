using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core.Results;

namespace Wf.Integration.Tests.Reports;

internal sealed class ReportReadFixture : IAsyncDisposable
{
    private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
    private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;
    public ReportPublishTests.ReportHost Host { get; private set; } = null!;
    public NpgsqlDataSource Source { get; }
    public Guid User { get; } = Guid.NewGuid();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ReportReadFixture(NpgsqlDataSource source)
    {
        Source = source;
        key = new(rsa) { KeyId = "report-read-fixture" };
    }

    public static async Task<ReportReadFixture> Start(NpgsqlDataSource source, IBlobStore? store = null,
        Action<WebApplicationBuilder>? services = null, Action<WebApplication>? configure = null)
    {
        var fixture = new ReportReadFixture(source);
        fixture.Host = await ReportPublishTests.ReportHost.Start(source, configureServices: builder =>
        {
            builder.Configuration["AzureAd:TenantId"] = Tenant;
            builder.Configuration["AzureAd:ClientId"] = Audience;
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddTeamAuthorization();
            if (store is not null) builder.Services.AddSingleton(store);
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(fixture.key);
            var manager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = metadata;
                options.ConfigurationManager = manager;
                options.TokenValidationParameters.IssuerValidator =
                    AadIssuerValidator.GetAadIssuerValidator(Issuer, null, _ => manager).Validate;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
            services?.Invoke(builder);
        }, configure: app =>
        {
            app.UseReportResponseHeaders();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapReportContentEndpoints();
            app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous();
            configure?.Invoke(app);
        });
        return fixture;
    }

    public async Task<ResultIdentity> Seed(DateTimeOffset? at = null, string? limits = null)
    {
        var attempt = await Host.Seed(at: at, limits: limits);
        await Execute("""
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
            SELECT $2,2,split_part(workflow_id,'/',1)||'/'||split_part(workflow_id,'/',2),0,$3,1
            FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1
            """, attempt.RunId, Guid.CreateVersion7(), User);
        return attempt;
    }

    public string Credential(Guid? user = null, string identity = "user", string role = "", bool expired = false,
        bool unsigned = false, Claim[]? extra = null)
    {
        var claims = new List<Claim>
        {
            new("tid", Tenant), new("ver", "2.0"), new("oid", (user ?? User).ToString("D")),
            new("idtyp", identity), new("roles", role)
        };
        if (identity == "user") claims.Add(new("scp", "api.read"));
        if (extra is not null) claims.AddRange(extra);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
            DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(expired ? -1 : 10),
            unsigned ? null : new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
    }

    public async Task<HttpResponseMessage> Get(string path, string? credential = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool anonymous = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!anonymous) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential ?? Credential());
        return await Host.Client.SendAsync(request, completion, Token);
    }

    public async Task Execute(string sql, params object[] values)
    {
        await using var command = Source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        rsa.Dispose();
    }
}
