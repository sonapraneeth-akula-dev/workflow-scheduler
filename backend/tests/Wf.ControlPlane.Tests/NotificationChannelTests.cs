using Microsoft.Extensions.Configuration;
using Wf.ControlPlane.Engine.Notifications;

namespace Wf.ControlPlane.Tests;

public sealed class NotificationChannelTests
{
    [Theory]
    [InlineData("Emails")]
    [InlineData("Teams")]
    public void Either_channel_can_enable_delivery_without_webhooks_and_matches_exact_scope_and_target(string channel)
    {
        using var options = Read(channel);
        Assert.True(options.Enabled);
        const string type = "team/type";
        if (channel == "Emails")
        {
            Assert.NotNull(options.FindEmail(type, "subscriber@example.test"));
            Assert.Null(options.FindEmail("foreign/type", "subscriber@example.test"));
            Assert.Null(options.FindEmail(type, "other@example.test"));
        }
        else
        {
            Assert.NotNull(options.FindTeams(type, "secret://notifications/teams"));
            Assert.Null(options.FindTeams("foreign/type", "secret://notifications/teams"));
            Assert.Null(options.FindTeams(type, "secret://notifications/other"));
        }
    }

    [Theory]
    [InlineData("TypeScope", "invalid")]
    [InlineData("Target", "Display Name <subscriber@example.test>")]
    [InlineData("Target", "user@example.test\r\nBcc: other@example.test")]
    [InlineData("From", "bad sender")]
    [InlineData("Host", "invalid/host")]
    [InlineData("Host", " relay.example.test")]
    [InlineData("Host", "169.254.169.254")]
    [InlineData("Host", "::ffff:127.0.0.1")]
    [InlineData("Host", "0.0.0.0")]
    [InlineData("Port", "0")]
    [InlineData("Port", "65536")]
    [InlineData("Port", "secret-invalid-port")]
    [InlineData("EnableSsl", "secret-invalid-bool")]
    [InlineData("Host", "smtp.example.test")]
    [InlineData("Host", "localhost")]
    [InlineData("Host", "10.0.0.1")]
    [InlineData("Username", "secret-user")]
    [InlineData("Password", "secret-password")]
    public void Invalid_email_configuration_does_not_expose_configuration_values(string field, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Read("Emails", field, value));
        Assert.DoesNotContain(value, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("TypeScope", "invalid")]
    [InlineData("Target", "https://workflow.example.test/secret-url")]
    [InlineData("Url", "http://remote.example.test/private")]
    [InlineData("Url", "https://user:secret-password@workflow.example.test/hook")]
    [InlineData("Url", "https://workflow.example.test/hook#secret-fragment")]
    [InlineData("Url", " https://workflow.example.test/hook")]
    [InlineData("Url", "https://workflow.example.test/\r\ncredential")]
    [InlineData("Url", "not-a-secret-url")]
    [InlineData("Addresses:0", "169.254.169.254")]
    [InlineData("Addresses:0", "0.0.0.0")]
    [InlineData("Addresses:0", "ff02::1")]
    [InlineData("Addresses:0", "workflow.example.test")]
    public void Invalid_Teams_configuration_does_not_expose_credential_URLs(string field, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Read("Teams", field, value));
        Assert.DoesNotContain(value, error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Teams_loopback_URL_requires_loopback_pins()
    {
        var values = Values("Teams");
        values["Notifications:Teams:0:Url"] = "http://localhost:8765/hook?sig=private";
        values["Notifications:Teams:0:Addresses:0"] = "10.0.0.1";
        Assert.Throws<InvalidOperationException>(() => Read(values));
    }

    [Fact]
    public void Email_authentication_requires_both_values_and_TLS_without_ambient_credentials()
    {
        var values = Values("Emails");
        values["Notifications:Emails:0:Username"] = "smtp-user";
        values["Notifications:Emails:0:Password"] = "smtp-private-password";
        Assert.Throws<InvalidOperationException>(() => Read(values));
        values["Notifications:Emails:0:EnableSsl"] = "true";
        values["Notifications:Emails:0:Host"] = "smtp.example.test";
        using var options = Read(values);
        Assert.NotNull(options.FindEmail("team/type", "subscriber@example.test"));
    }

    [Theory]
    [InlineData("Emails")]
    [InlineData("Teams")]
    public void Duplicate_and_more_than_64_destinations_fail(string channel)
    {
        var values = Values(channel);
        foreach (var (key, value) in Values(channel).Where(pair => pair.Key.Contains(":0:", StringComparison.Ordinal)))
            values[key.Replace(":0:", ":1:", StringComparison.Ordinal)] = value;
        Assert.Throws<InvalidOperationException>(() => Read(values));
        for (var i = 2; i <= 64; i++)
            values[$"Notifications:{channel}:{i}:TypeScope"] = "team/type";
        Assert.Throws<InvalidOperationException>(() => Read(values));
    }

    private static NotificationOptions Read(string channel, string? field = null, string? value = null)
    {
        var values = Values(channel);
        if (field is not null) values[$"Notifications:{channel}:0:{field}"] = value;
        return Read(values);
    }

    private static NotificationOptions Read(Dictionary<string, string?> values) =>
        NotificationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    private static Dictionary<string, string?> Values(string channel) => channel == "Emails" ? new()
    {
        ["Notifications:Enabled"] = "true", ["Notifications:Emails:0:TypeScope"] = "team/type",
        ["Notifications:Emails:0:Target"] = "subscriber@example.test", ["Notifications:Emails:0:From"] = "kardia@example.test",
        ["Notifications:Emails:0:Host"] = "127.0.0.1", ["Notifications:Emails:0:EnableSsl"] = "false"
    } : new()
    {
        ["Notifications:Enabled"] = "true", ["Notifications:Teams:0:TypeScope"] = "team/type",
        ["Notifications:Teams:0:Target"] = "secret://notifications/teams",
        ["Notifications:Teams:0:Url"] = "https://workflow.example.test/hook?sig=private-credential",
        ["Notifications:Teams:0:Addresses:0"] = "127.0.0.1"
    };
}
