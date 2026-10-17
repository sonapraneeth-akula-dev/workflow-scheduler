using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Wf.ControlPlane.Engine.Notifications;

namespace Wf.ControlPlane.Tests;

public sealed class CloudEventSignerTests
{
    [Fact]
    public void Exact_timestamp_dot_and_wire_bytes_are_signed_without_JSON_reserialization()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var body = Encoding.UTF8.GetBytes("""{"specversion":"1.0","subject":"caf\u00e9"}""");
        const string timestamp = "1792224000";
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key,
            Encoding.UTF8.GetBytes(timestamp + "." + Encoding.UTF8.GetString(body))));
        Assert.Equal(expected, CloudEventSigner.Sign(key, timestamp, body));
        Assert.NotEqual(expected, CloudEventSigner.Sign(key, "1792224001", body));
        Assert.NotEqual(expected, CloudEventSigner.Sign(key, timestamp, [.. body, (byte)' ']));
        key[0] = 255;
        Assert.NotEqual(expected, CloudEventSigner.Sign(key, timestamp, body));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(129)]
    public void Weak_or_unbounded_signing_keys_fail(int bytes) =>
        Assert.Throws<ArgumentException>(() => CloudEventSigner.Sign(new byte[bytes], "1", "{}"u8));

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public void Noncanonical_timestamps_fail(string timestamp) =>
        Assert.Throws<ArgumentException>(() => CloudEventSigner.Sign(new byte[32], timestamp, "{}"u8));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(7, 60)]
    [InlineData(8, 60)]
    public void Retry_backoff_is_exponential_and_capped(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), NotificationDispatcher.RetryDelay(attempt));

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Invalid_retry_counts_fail(int attempt) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationDispatcher.RetryDelay(attempt));

    [Fact]
    public void Default_configuration_disables_delivery_without_accepting_arbitrary_destinations()
    {
        using var options = NotificationOptions.Read(new ConfigurationBuilder().Build());
        Assert.False(options.Enabled);
        Assert.Null(options.Find("team/type", "https://notify.example.test/hook", "secret://notifications/signing"));
        Assert.Throws<InvalidOperationException>(() => NotificationOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Enabled"] = "true" }).Build()));
    }

    [Fact]
    public void Approved_configuration_matches_exact_owning_type_destination_and_secret_reference()
    {
        using var options = Read();
        Assert.True(options.Enabled);
        var destination = options.Find("team/type", "https://notify.example.test/hook", "secret://notifications/signing");
        Assert.NotNull(destination);
        Assert.Null(options.Find("foreign/type", destination.Target, destination.SecretRef));
        Assert.Null(options.Find("team/type", destination.Target + "/other", destination.SecretRef));
        Assert.Null(options.Find("team/type", destination.Target, "secret://notifications/other"));
    }

    [Theory]
    [InlineData("TypeScope", "team/type/workflow")]
    [InlineData("Target", "http://remote.example.test/hook")]
    [InlineData("Target", "https://notify.example.test/hook?secret=inline")]
    [InlineData("Target", "https://notify.example.test/hook#fragment")]
    [InlineData("SecretRef", "literal signing key")]
    [InlineData("SigningKeyBase64", "not base64")]
    [InlineData("SigningKeyBase64", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("Addresses:0", "0.0.0.0")]
    [InlineData("Addresses:0", "::")]
    [InlineData("Addresses:0", "::ffff:127.0.0.1")]
    [InlineData("Addresses:0", "169.254.169.254")]
    [InlineData("Addresses:0", "168.63.129.16")]
    [InlineData("Addresses:0", "fd00:ec2::254")]
    [InlineData("Addresses:0", "fe80::1")]
    [InlineData("Addresses:0", "ff02::1")]
    [InlineData("Addresses:0", "224.0.0.1")]
    [InlineData("Addresses:0", "127.1")]
    [InlineData("Addresses:0", "notify.example.test")]
    public void Unsafe_configuration_fails_without_echoing_values_or_an_inner_exception(string field, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Read(field, value));
        Assert.DoesNotContain(value, error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Loopback_HTTP_cannot_be_redirected_to_a_nonloopback_pinned_address()
    {
        var values = Values();
        values["Notifications:Webhooks:0:Target"] = "http://localhost:8765/hook";
        values["Notifications:Webhooks:0:Addresses:0"] = "10.0.0.1";
        Assert.Throws<InvalidOperationException>(() => NotificationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    private static NotificationOptions Read(string? field = null, string? value = null)
    {
        var values = Values();
        if (field is not null) values["Notifications:Webhooks:0:" + field] = value;
        return NotificationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static Dictionary<string, string?> Values() => new()
    {
        ["Notifications:Enabled"] = "true",
        ["Notifications:Webhooks:0:TypeScope"] = "team/type",
        ["Notifications:Webhooks:0:Target"] = "https://notify.example.test/hook",
        ["Notifications:Webhooks:0:SecretRef"] = "secret://notifications/signing",
        ["Notifications:Webhooks:0:SigningKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Notifications:Webhooks:0:Addresses:0"] = "127.0.0.1"
    };
}
