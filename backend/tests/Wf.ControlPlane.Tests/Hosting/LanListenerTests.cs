using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Tests.Hosting;

public sealed class LanListenerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wf-lan-unit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Default_profile_never_loads_a_certificate()
    {
        var builder = Builder();
        builder.Configuration["Lan:Enabled"] = "false";
        Assert.Null(LanListener.Read(builder.Configuration, builder.Environment));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Lan_cannot_enable_a_production_development_bypass(string environment)
    {
        var builder = Builder(environment);
        Assert.Throws<InvalidOperationException>(() => LanListener.Read(builder.Configuration, builder.Environment));
    }

    [Theory]
    [InlineData("Lan:Address", "127.0.0.1")]
    [InlineData("Lan:Address", "0.0.0.0")]
    [InlineData("Lan:Address", "0.1.2.3")]
    [InlineData("Lan:Address", "255.255.255.255")]
    [InlineData("Lan:Address", "224.0.0.1")]
    [InlineData("Lan:Address", "localhost")]
    [InlineData("Lan:Address", "::1")]
    [InlineData("Lan:Address", "10.1")]
    [InlineData("Lan:Port", "0")]
    [InlineData("Lan:Port", "65536")]
    [InlineData("Lan:Certificate", "")]
    [InlineData("Lan:Key", "")]
    public void Invalid_configuration_is_rejected(string name, string value)
    {
        var builder = Builder();
        builder.Configuration[name] = value;
        Assert.Throws<InvalidOperationException>(() => LanListener.Read(builder.Configuration, builder.Environment));
    }

    [Theory]
    [InlineData("different-ip")]
    [InlineData("dns-not-ip")]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    public void Certificate_must_be_current_and_have_the_exact_IP_SAN(string fault)
    {
        Certificate(fault);
        var builder = Builder();
        Assert.Throws<InvalidOperationException>(() => LanListener.Read(builder.Configuration, builder.Environment));
    }

    [Fact]
    public void Valid_PEM_with_IP_SAN_configures_the_exact_endpoint()
    {
        Certificate();
        var builder = Builder();
        using var listener = LanListener.Read(builder.Configuration, builder.Environment);
        Assert.NotNull(listener);
        Assert.Equal(IPAddress.Parse("10.26.2.225"), listener.Address);
        Assert.Equal(5001, listener.Port);
        Assert.Equal("https://10.26.2.225:5001/", listener.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData(false, false, "10.26.2.225", false)]
    [InlineData(false, true, "10.26.2.225", false)]
    [InlineData(true, false, "10.26.2.225", false)]
    [InlineData(true, true, "10.26.2.224", false)]
    [InlineData(true, true, "10.26.2.225", true)]
    public async Task Remote_development_identity_requires_explicit_LAN_and_exact_TLS_listener(
        bool enabled, bool tls, string localAddress, bool accepted)
    {
        var builder = Builder();
        builder.Configuration["Authentication:Mode"] = "Development";
        builder.Configuration["Lan:Enabled"] = enabled.ToString();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.26.2.99");
            context.Connection.LocalIpAddress = IPAddress.Parse(localAddress);
            context.Request.Scheme = tls ? "https" : "http";
            return next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/admin", () => Results.Ok()).RequireAuthorization(AuthExtensions.Admin);
        app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses))
        };
        using var response = await client.GetAsync("/admin", TestContext.Current.CancellationToken);
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        using var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private WebApplicationBuilder Builder(string environment = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lan:Enabled"] = "true", ["Lan:Address"] = "10.26.2.225",
            ["Lan:Certificate"] = Path.Combine(root, "cert.pem"), ["Lan:Key"] = Path.Combine(root, "key.pem")
        });
        return builder;
    }

    private void Certificate(string fault = "")
    {
        Directory.CreateDirectory(root);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=local LAN tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        if (fault == "dns-not-ip") san.AddDnsName("10.26.2.225");
        else san.AddIpAddress(IPAddress.Parse(fault == "different-ip" ? "10.26.2.224" : "10.26.2.225"));
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(fault == "not-yet-valid" ? 1 : -2),
            DateTimeOffset.UtcNow.AddDays(fault == "expired" ? -1 : 2));
        File.WriteAllText(Path.Combine(root, "cert.pem"), cert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(root, "key.pem"), key.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
