using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api.Subscriptions;

namespace Wf.ControlPlane.Tests;

public sealed class SubscriptionEndpointsTests
{
    private static SubscriptionRequest Request() => new("workflow", "team/type/workflow", ["Completed", "Failed"],
        "Webhook", "https://notify.example.test/hook", "secret://notifications/signing");

    [Theory]
    [InlineData("workflow", "team/type/workflow")]
    [InlineData("workflow", "team/type/caf\u00e9")]
    [InlineData("type", "team/type")]
    [InlineData("schedule", "019ed132-2f44-7000-8000-000000000001")]
    public void Exact_scope_identities_are_supported(string kind, string id) =>
        Assert.Equal(new(kind, id), SubscriptionScope.Parse(kind, id));

    [Theory]
    [InlineData("unknown", "team/type/workflow")]
    [InlineData("workflow", "team/type")]
    [InlineData("workflow", "team/type/workflow/extra")]
    [InlineData("workflow", "team/../workflow")]
    [InlineData("type", "team/type/workflow")]
    [InlineData("type", "team/\n")]
    [InlineData("schedule", "00000000-0000-0000-0000-000000000000")]
    [InlineData("schedule", "019ED132-2F44-7000-8000-000000000001")]
    public void Invalid_scopes_are_not_normalized_or_guessed(string kind, string id) =>
        Assert.Throws<ArgumentException>(() => SubscriptionScope.Parse(kind, id));

    [Fact]
    public void Event_order_is_canonical_without_mutating_the_caller_array()
    {
        var request = Request() with { Events = ["Failed", "Completed"] };
        Assert.Equal(["Completed", "Failed"], request.Validate().Events);
        Assert.Equal(["Failed", "Completed"], request.Events);
        Assert.Equal("https://notify.example.test/hook", request.Validate().Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("completed")]
    [InlineData("Succeeded")]
    [InlineData("Completed,Failed")]
    public void Unknown_event_names_fail(string value) =>
        Assert.Throws<ArgumentException>(() => (Request() with { Events = [value] }).Validate());

    [Fact]
    public void Empty_duplicate_and_null_event_lists_fail()
    {
        foreach (var events in new[] { Array.Empty<string>(), ["Completed", "Completed"], new string[] { null! } })
            Assert.Throws<ArgumentException>(() => (Request() with { Events = events }).Validate());
    }

    [Theory]
    [InlineData("http://remote.example.test/hook")]
    [InlineData("https://user:password@notify.example.test/hook")]
    [InlineData("https://notify.example.test/hook?token=literal")]
    [InlineData("https://notify.example.test/hook#fragment")]
    [InlineData("file:///etc/passwd")]
    [InlineData(" https://notify.example.test/hook")]
    [InlineData("https://notify.example.test/\r\ninjected")]
    public void Unsafe_or_credential_bearing_webhook_destinations_fail_without_echoing_them(string target)
    {
        var error = Assert.Throws<ArgumentException>(() => (Request() with { Target = target }).Validate());
        Assert.DoesNotContain(target, error.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765/hook")]
    [InlineData("http://[::1]:8765/hook")]
    [InlineData("https://notify.example.test/hook")]
    public void Webhook_destinations_support_local_loopback_without_general_cleartext_targets(string target) =>
        Assert.Equal(target, (Request() with { Target = target }).Validate().Target);

    [Theory]
    [InlineData(null)]
    [InlineData("literal key with spaces")]
    [InlineData("secret://notifications/../signing")]
    public void Webhook_requires_a_safe_secret_reference_not_inline_key_material(string? reference) =>
        Assert.Throws<ArgumentException>(() => (Request() with { SecretRef = reference }).Validate());

    [Theory]
    [InlineData("Email", "subscriber@example.test")]
    [InlineData("Teams", "secret://notifications/teams-channel")]
    public void Email_and_Teams_have_explicit_destination_forms(string channel, string target) =>
        Assert.Equal(target, (Request() with { Channel = channel, Target = target, SecretRef = null }).Validate().Target);

    [Theory]
    [InlineData("Email", "Name <subscriber@example.test>")]
    [InlineData("Email", "first@example.test,second@example.test")]
    [InlineData("Teams", "https://connector.example.test/secret")]
    [InlineData("Unknown", "destination")]
    public void Ambiguous_or_inline_credential_destinations_fail(string channel, string target) =>
        Assert.Throws<ArgumentException>(() => (Request() with { Channel = channel, Target = target, SecretRef = null }).Validate());

    [Theory]
    [InlineData("""{"scope":"workflow","scopeId":"t/y/w","events":["Completed"],"channel":"Email","target":"one@example.test","extra":"private"}""")]
    [InlineData("""{"scope":"workflow","scope":"type","scopeId":"t/y/w","events":["Completed"],"channel":"Email","target":"one@example.test"}""")]
    [InlineData("""{"scope":"workflow","scopeId":"t/y/w","events":null,"channel":"Email","target":"one@example.test"}""")]
    [InlineData("""{"scope":"workflow","scopeId":"t/y/w","events":["Completed"],"channel":"Email","target":null}""")]
    [InlineData("""{"scope":"workflow","scopeId":"t/y/w","channel":"Email","target":"one@example.test"}""")]
    public void Generated_request_contract_rejects_unknown_duplicate_null_and_missing_members(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SubscriptionJson.Default.SubscriptionRequest));

    [Fact]
    public void Listing_is_bounded_with_strict_optional_scope_pairs_and_canonical_UUID_keysets()
    {
        Assert.Equal(new(null, 50, null), SubscriptionQuery.Parse(QueryCollection.Empty));
        var id = Guid.NewGuid();
        var query = SubscriptionQuery.Parse(Query(new()
            { ["scope"] = "type", ["scopeId"] = "team/type", ["limit"] = "100", ["cursor"] = id.ToString("D") }));
        Assert.Equal(new("type", "team/type"), query.Scope);
        Assert.Equal(100, query.Limit);
        Assert.Equal(id, query.After);
    }

    [Theory]
    [InlineData("scope", "type")]
    [InlineData("scopeId", "team/type")]
    [InlineData("limit", "0")]
    [InlineData("limit", "101")]
    [InlineData("limit", "01")]
    [InlineData("cursor", "not-a-uuid")]
    [InlineData("principalId", "other-user")]
    public void Invalid_filters_are_explicit_not_silently_dropped(string key, string value) =>
        Assert.Throws<ArgumentException>(() => SubscriptionQuery.Parse(Query(new() { [key] = value })));

    [Fact]
    public void Duplicate_filters_fail() => Assert.Throws<ArgumentException>(() => SubscriptionQuery.Parse(
        Query(new() { ["limit"] = new(["1", "2"]) })));

    private static QueryCollection Query(Dictionary<string, StringValues> values) => new(values);
}
