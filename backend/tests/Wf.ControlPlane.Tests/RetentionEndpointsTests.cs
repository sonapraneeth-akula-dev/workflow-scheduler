using System.Text.Json;
using Wf.ControlPlane.Api.Admin;

namespace Wf.ControlPlane.Tests;

public sealed class RetentionEndpointsTests
{
    [Theory]
    [InlineData(1, 365, 1)]
    [InlineData(90, 365, 30)]
    [InlineData(36500, 36500, 36500)]
    public void Valid_days_and_exact_type_overrides_round_trip(int days, int audit, int perType)
    {
        var policy = new RetentionPolicy(days, audit, new() { ["team/type"] = perType });
        var json = JsonSerializer.Serialize(policy.Validate(), RetentionJson.Default.RetentionPolicy);
        Assert.Contains("\"audit_days\":", json);
        var read = JsonSerializer.Deserialize(json, RetentionJson.Default.RetentionPolicy)!.Validate();
        Assert.Equal((days, audit, perType), (read.DefaultDays, read.AuditDays, read.PerType["team/type"]));
    }

    [Theory]
    [InlineData(0, 365, 30)]
    [InlineData(-1, 365, 30)]
    [InlineData(36501, 365, 30)]
    [InlineData(90, 30, 30)]
    [InlineData(90, 364, 30)]
    [InlineData(90, 36501, 30)]
    [InlineData(90, 365, 0)]
    [InlineData(90, 365, -1)]
    [InlineData(90, 365, 36501)]
    public void Out_of_range_days_and_short_audit_retention_fail(int days, int audit, int perType) =>
        Assert.Throws<ArgumentException>(() => new RetentionPolicy(days, audit, new() { ["team/type"] = perType }).Validate());

    [Theory]
    [InlineData("")]
    [InlineData("type")]
    [InlineData("team/type/workflow")]
    [InlineData("team/..")]
    [InlineData("team/type\n")]
    public void Invalid_type_scope_is_not_normalized(string type) =>
        Assert.Throws<ArgumentException>(() => new RetentionPolicy(90, 365, new() { [type] = 30 }).Validate());

    [Fact]
    public void Override_count_is_bounded_and_null_is_not_a_default()
    {
        Assert.Throws<ArgumentException>(() => new RetentionPolicy(90, 365, null!).Validate());
        new RetentionPolicy(90, 365, Enumerable.Range(0, 256).ToDictionary(i => "team/type" + i, _ => 30)).Validate();
        Assert.Throws<ArgumentException>(() => new RetentionPolicy(90, 365,
            Enumerable.Range(0, 257).ToDictionary(i => "team/type" + i, _ => 30)).Validate());
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"default_days":90,"audit_days":365}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":null}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":{},"extra":1}""")]
    [InlineData("""{"default_days":90,"default_days":30,"audit_days":365,"per_type":{}}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":{"team/type":30,"team/type":60}}""")]
    [InlineData("""{"default_days":"90","audit_days":365,"per_type":{}}""")]
    [InlineData("""{"default_days":1.5,"audit_days":365,"per_type":{}}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":{"team/type":null}}""")]
    public void Generated_contract_rejects_missing_unknown_duplicate_null_and_wrongly_typed_values(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, RetentionJson.Default.RetentionPolicy));
}
