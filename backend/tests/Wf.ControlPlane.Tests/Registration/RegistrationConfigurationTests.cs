using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wf.ControlPlane.Registration;

namespace Wf.ControlPlane.Tests.Registration;

public sealed class RegistrationConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not-base64")]
    [InlineData("YQ==")]
    public void Missing_malformed_and_short_upload_keys_fail_at_startup_without_echo(string? key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:UploadHmacKey"] = key }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkflowRegistrations(configuration));
        Assert.Equal("Storage:UploadHmacKey must be a base64-encoded key of at least 256 random bits.", error.Message);
    }
}
