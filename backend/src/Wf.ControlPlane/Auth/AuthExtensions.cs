using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Auth;

public static class AuthExtensions
{
    public const string Admin = "Admin";
    public const string Registrar = "Registrar";
    public const string Agent = "Agent";
    private const string DevelopmentScheme = "Development";

    public static IServiceCollection AddWorkflowAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var mode = configuration["Authentication:Mode"] ?? "Entra";
        if (mode == DevelopmentScheme)
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Development authentication is permitted only in the Development environment.");
            services.AddAuthentication(DevelopmentScheme)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentScheme, _ => { });
        }
        else if (mode == "Entra")
        {
            var azureAd = configuration.GetSection("AzureAd");
            if (!Guid.TryParseExact(azureAd["TenantId"], "D", out var tenantId) || tenantId == Guid.Empty)
                throw new InvalidOperationException("AzureAd:TenantId must be a non-empty tenant UUID.");
            if (!Guid.TryParseExact(azureAd["ClientId"], "D", out var clientId) || clientId == Guid.Empty)
                throw new InvalidOperationException("AzureAd:ClientId must be a non-empty API application UUID.");
            if (!Uri.TryCreate(azureAd["Instance"] ?? "https://login.microsoftonline.com/", UriKind.Absolute,
                    out var instance) || instance.Scheme != Uri.UriSchemeHttps || instance.UserInfo.Length != 0 ||
                instance.AbsolutePath != "/" || instance.Query.Length != 0 || instance.Fragment.Length != 0)
                throw new InvalidOperationException("AzureAd:Instance must be an HTTPS authority origin.");
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(options =>
                {
                    azureAd.Bind(options);
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters.RoleClaimType = "roles";
                    options.TokenValidationParameters.NameClaimType = "name";
                    options.IncludeErrorDetails = false;
                    options.RequireHttpsMetadata = true;
                    options.Events.OnMessageReceived = context =>
                    {
                        if (context.Request.Path.StartsWithSegments(LiveHub.Path) &&
                            !context.Request.Headers.ContainsKey("Authorization") &&
                            context.Request.Query["access_token"] is { Count: 1 } accessToken)
                            context.Token = accessToken[0];
                        return Task.CompletedTask;
                    };
                    options.Events.OnTokenValidated = context =>
                    {
                        if (!Guid.TryParse(context.Principal?.FindFirstValue("tid"), out var tokenTenant) || tokenTenant != tenantId)
                            context.Fail("The token tenant does not match the configured tenant.");
                        return Task.CompletedTask;
                    };
                }, options =>
                {
                    azureAd.Bind(options);
                    options.Instance = instance.AbsoluteUri;
                    options.TenantId = tenantId.ToString("D");
                    options.ClientId = clientId.ToString("D");
                });
        }
        else
            throw new InvalidOperationException("Authentication:Mode must be Entra or Development.");

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().RequireRole(Admin));
            options.AddPolicy(Registrar, policy => policy.RequireAuthenticatedUser().RequireRole(Registrar)
                .RequireClaim("idtyp", "app").RequireAssertion(context => !context.User.HasClaim(claim => claim.Type == "scp")));
            options.AddPolicy(Agent, policy => policy.RequireAuthenticatedUser().RequireRole(Agent)
                .RequireClaim("idtyp", "app").RequireAssertion(context => !context.User.HasClaim(claim => claim.Type == "scp")));
        });
        return services;
    }

    private sealed class DevelopmentAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim("oid", "00000000-0000-0000-0000-000000000001"),
                new Claim("name", "Local development"),
                new Claim("idtyp", "app"),
                new Claim("roles", Admin),
                new Claim("roles", Registrar),
                new Claim("roles", Agent)
            ], Scheme.Name, "name", "roles");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
