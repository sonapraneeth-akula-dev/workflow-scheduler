using System.Text.Json;
using Wf.ControlPlane.Api.Admin;

namespace Wf.ControlPlane.Tests;

public sealed class SystemNoticeTests
{
    [Theory]
    [InlineData("Info")]
    [InlineData("Warning")]
    [InlineData("Critical")]
    public void Plain_text_including_markup_round_trips_without_interpretation(string severity)
    {
        var notice = new SystemNotice("<script>alert('x')</script>\nMaintenance\twindow", severity, null).Validate();
        var json = JsonSerializer.Serialize(notice, SystemNoticeJson.Default.SystemNotice);
        Assert.Equal(notice, JsonSerializer.Deserialize(json, SystemNoticeJson.Default.SystemNotice));
        new SystemNotice(new string('x', 2000), severity, DateTimeOffset.UtcNow.AddDays(1)).Validate(DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("", "Info")]
    [InlineData(" \r\n\t", "Info")]
    [InlineData("hello\0world", "Info")]
    [InlineData("hello\u001bworld", "Warning")]
    [InlineData("hello", "warning")]
    [InlineData("hello", "Success")]
    public void Blank_control_characters_and_unknown_severity_are_rejected(string message, string severity) =>
        Assert.Throws<ArgumentException>(() => new SystemNotice(message, severity, null).Validate());

    [Fact]
    public void Message_and_expiry_limits_are_enforced()
    {
        Assert.Throws<ArgumentException>(() => new SystemNotice(new string('x', 2001), "Info", null).Validate());
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new SystemNotice("Expired", "Info", now).Validate(now));
        Assert.Throws<ArgumentException>(() => new SystemNotice("Expired", "Info", now.AddTicks(-1)).Validate(now));
        new SystemNotice("Stored expired notice", "Info", now.AddDays(-1)).Validate();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"message":"test","severity":"Info"}""")]
    [InlineData("""{"message":null,"severity":"Info","expiresAt":null}""")]
    [InlineData("""{"message":"test","severity":1,"expiresAt":null}""")]
    [InlineData("""{"message":"test","severity":"Info","expiresAt":"tomorrow"}""")]
    [InlineData("""{"message":"test","severity":"Info","expiresAt":null,"extra":1}""")]
    [InlineData("""{"message":"test","message":"other","severity":"Info","expiresAt":null}""")]
    public void Strict_contract_rejects_missing_duplicate_unknown_null_or_wrong_types(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SystemNoticeJson.Default.SystemNotice));
}
