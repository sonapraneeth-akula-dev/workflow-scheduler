using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Hosting;
using Wf.ControlPlane.Engine.Leadership;

namespace Wf.ControlPlane.Tests.Hosting;

public sealed class LocalComposeOptionsTests
{
    [Theory]
    [InlineData("Production", "true", "ppe", "false")]
    [InlineData("Staging", "true", "dev", "false")]
    [InlineData("Development", "false", "ppe", "false")]
    [InlineData("Development", "true", "production", "false")]
    [InlineData("Development", "true", "PPE", "false")]
    [InlineData("Development", "true", "ppe", "true")]
    public void Compose_bypass_requires_an_explicit_supported_Development_container(
        string environment, string container, string name, string lan)
    {
        var builder = Builder(environment, name);
        builder.Configuration["DOTNET_RUNNING_IN_CONTAINER"] = container;
        builder.Configuration["Lan:Enabled"] = lan;
        Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment));
    }

    [Fact]
    public void Disabled_profile_never_changes_the_production_listener_or_authentication()
    {
        var builder = Builder("Production", "ppe");
        builder.Configuration["LocalCompose:Enabled"] = "false";
        Assert.Null(LocalComposeOptions.Read(builder.Configuration, builder.Environment));
        Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment));
    }

    [Theory]
    [InlineData("dev", "172.30.62.3", "172.30.62.1", true)]
    [InlineData("ppe", "172.30.67.3", "172.30.67.1", true)]
    [InlineData("dev", "172.30.62.3", "172.30.67.1", false)]
    [InlineData("ppe", "172.30.62.3", "172.30.67.1", false)]
    [InlineData("ppe", "172.30.67.3", "203.0.113.5", false)]
    [InlineData("ppe", "172.30.67.3", "10.1.2.3", false)]
    public async Task Development_identity_requires_both_actual_addresses_on_the_environment_bridge(
        string name, string local, string remote, bool allowed)
    {
        var builder = Builder("Development", name);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.LocalIpAddress = IPAddress.Parse(local);
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/private", () => "local").RequireAuthorization(AuthExtensions.Agent);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient
        {
            BaseAddress = new(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses))
        };
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "172.30.67.1");
        using var response = await client.GetAsync("/private", TestContext.Current.CancellationToken);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://wf-api:8080", true)]
    [InlineData("http://wf-api:8080/", true)]
    [InlineData("http://wf-api:8081", false)]
    [InlineData("http://wf-api.example:8080", false)]
    [InlineData("http://wf-api:8080/path", false)]
    [InlineData("https://wf-api:8080", false)]
    public void Plaintext_package_origin_is_the_exact_private_API_origin(string value, bool allowed) =>
        Assert.Equal(allowed, LocalComposeOptions.IsPackageOrigin(new(value)));

    [Theory]
    [InlineData("http://wf-engine-a:8081", true)]
    [InlineData("http://wf-engine-b:8081", true)]
    [InlineData("http://wf-engine-a:8080", false)]
    [InlineData("http://wf-engine-a.example:8081", false)]
    [InlineData("http://wf-api:8081", false)]
    public void Nonloopback_plaintext_leader_hints_require_the_private_Compose_profile(string value, bool allowed)
    {
        using var source = NpgsqlDataSource.Create("Host=unused;Database=unused");
        var options = new LeaseOptions("test", new(value));
        Assert.Throws<ArgumentException>(() => new LeaseManager(source, options, NullLogger<LeaseManager>.Instance));
        var builder = Builder("Development", "ppe");
        var compose = LocalComposeOptions.Read(builder.Configuration, builder.Environment);
        if (allowed)
        {
            using var manager = new LeaseManager(source, options, NullLogger<LeaseManager>.Instance, compose: compose);
        }
        else
            Assert.Throws<ArgumentException>(() => new LeaseManager(source, options,
                NullLogger<LeaseManager>.Instance, compose: compose));
    }

    private static WebApplicationBuilder Builder(string environment, string name)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalCompose:Enabled"] = "true",
            ["LocalCompose:Environment"] = name,
            ["DOTNET_RUNNING_IN_CONTAINER"] = "true",
            ["Authentication:Mode"] = "Development"
        });
        return builder;
    }
}
