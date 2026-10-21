using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wf.ControlPlane.Registration;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Tests.Storage;

public sealed class BlobStoreConfigurationTests
{
    [Fact]
    public void Default_preserves_the_configured_filesystem_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-blob-configuration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = Configuration();
            configuration["Storage:Provider"] = null;
            configuration["Storage:Root"] = root;
            using var store = Assert.IsType<FileSystemBlobStore>(Create(configuration));
            Assert.True(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("5af1e760-c6e0-4508-9a99-4bd126c65755")]
    public void Azure_provider_resolves_through_the_actual_registration_wiring(string? clientId)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddConfiguration(Configuration());
        builder.Configuration["Storage:Azure:ManagedIdentityClientId"] = clientId;
        builder.Services.AddWorkflowRegistrations(builder.Configuration, builder.Environment);
        using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<AzureBlobStore>(services.GetRequiredService<IBlobStore>());
        Assert.Same(services.GetRequiredService<IBlobStore>(), services.GetRequiredService<IBlobStore>());
    }

    [Theory]
    [InlineData("Storage:Provider", "unknown")]
    [InlineData("Storage:Azure:Container", "")]
    [InlineData("Storage:Azure:Container", "ab")]
    [InlineData("Storage:Azure:Container", "Uppercase")]
    [InlineData("Storage:Azure:Container", "-blobs")]
    [InlineData("Storage:Azure:Container", "blobs-")]
    [InlineData("Storage:Azure:Container", "two--hyphens")]
    [InlineData("Storage:Azure:Container", "blobs/path")]
    [InlineData("Storage:Azure:Container", "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl")]
    [InlineData("Storage:Azure:ServiceUri", "")]
    [InlineData("Storage:Azure:ServiceUri", "http://account.blob.core.windows.net")]
    [InlineData("Storage:Azure:ServiceUri", "https://user@account.blob.core.windows.net")]
    [InlineData("Storage:Azure:ServiceUri", "https://account.blob.core.windows.net/path")]
    [InlineData("Storage:Azure:ServiceUri", "https://account.blob.core.windows.net/?sas=secret")]
    [InlineData("Storage:Azure:ServiceUri", "https://account.blob.core.windows.net/#fragment")]
    [InlineData("Storage:Azure:ManagedIdentityClientId", "")]
    [InlineData("Storage:Azure:ManagedIdentityClientId", "not-a-uuid")]
    [InlineData("Storage:Azure:ManagedIdentityClientId", "00000000-0000-0000-0000-000000000000")]
    public void Invalid_configuration_fails_without_a_filesystem_fallback(string name, string value)
    {
        var configuration = Configuration();
        configuration[name] = value;
        Assert.Throws<InvalidOperationException>(() => Create(configuration));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void Emulator_credentials_are_never_enabled_implicitly_or_in_production(string? environment)
    {
        var configuration = Emulator();
        Assert.Throws<InvalidOperationException>(() => Create(configuration, environment));
    }

    [Fact]
    public void Explicit_development_emulator_resolves()
    {
        Assert.IsType<AzureBlobStore>(Create(Emulator(), "Development"));
    }

    [Theory]
    [InlineData("Storage:Azure:ServiceUri", "https://account.blob.core.windows.net")]
    [InlineData("Storage:Azure:ManagedIdentityClientId", "5af1e760-c6e0-4508-9a99-4bd126c65755")]
    public void Conflicting_authentication_modes_fail(string name, string value)
    {
        var configuration = Emulator();
        configuration[name] = value;
        Assert.Throws<InvalidOperationException>(() => Create(configuration, "Development"));
    }

    [Fact]
    public void Invalid_emulator_credentials_are_not_echoed_in_errors()
    {
        var configuration = Emulator();
        configuration["Storage:Azure:EmulatorConnectionString"] = "AccountKey=must-not-leak";
        var error = Assert.Throws<InvalidOperationException>(() => Create(configuration, "Development"));
        Assert.DoesNotContain("must-not-leak", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_plaintext_emulator_is_rejected()
    {
        var configuration = Emulator();
        configuration["Storage:Azure:EmulatorConnectionString"] =
            configuration["Storage:Azure:EmulatorConnectionString"]!.Replace("127.0.0.1", "192.168.247.11", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Create(configuration, "Development"));
    }

    private static IBlobStore Create(IConfiguration configuration, string? environment = null) =>
        BlobStoreConfiguration.Create(configuration, RandomNumberGenerator.GetBytes(32), TimeProvider.System,
            environment is null ? null : WebApplication.CreateSlimBuilder(new WebApplicationOptions
                { EnvironmentName = environment }).Environment);

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "AzureBlob",
            ["Storage:Azure:ServiceUri"] = "https://account.blob.core.windows.net",
            ["Storage:Azure:Container"] = "kardia-blobs",
            ["Storage:UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }).Build();

    private static IConfigurationRoot Emulator()
    {
        var configuration = Configuration();
        configuration["Storage:Azure:ServiceUri"] = null;
        configuration["Storage:Azure:EmulatorConnectionString"] =
            "DefaultEndpointsProtocol=http;AccountName=lab;AccountKey=" +
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) + ";BlobEndpoint=http://127.0.0.1:10000/lab;";
        return configuration;
    }
}
