using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Tests;

public sealed class UserSchedulesTests
{
    private static readonly Guid Spa = Guid.Parse("60d24b19-f65a-4ed4-96cc-9174bfcacabc");

    [Fact]
    public void SPA_gate_accepts_only_authenticated_delegated_users_from_the_exact_configured_client()
    {
        var client = new UserScheduleClient(Spa);
        Assert.True(client.Allows(Caller()));
        Assert.True(client.Allows(Caller(claims => claims.RemoveAll(claim => claim.Type == "idtyp"))));
        Assert.False(new UserScheduleClient(Guid.NewGuid()).Allows(Caller()));
        Assert.False(new UserScheduleClient(Guid.Empty).Allows(Caller()));
        Assert.False(client.Allows(new(new ClaimsIdentity())));
    }

    [Theory]
    [InlineData("azp", null)]
    [InlineData("azp", "other-client")]
    [InlineData("azp", "00000000-0000-0000-0000-000000000000")]
    [InlineData("scp", null)]
    [InlineData("scp", " ")]
    [InlineData("idtyp", "app")]
    [InlineData("idtyp", "unexpected")]
    [InlineData("oid", null)]
    [InlineData("oid", "not-an-object-id")]
    [InlineData("oid", "00000000-0000-0000-0000-000000000000")]
    public void SPA_gate_rejects_missing_malformed_and_app_only_claims_without_appid_fallback(string claimType, string? value)
    {
        var caller = Caller(claims =>
        {
            claims.RemoveAll(claim => claim.Type == claimType);
            if (value is not null) claims.Add(new(claimType, value));
            claims.Add(new("appid", Spa.ToString("D")));
            claims.Add(new("roles", "Admin"));
        });
        Assert.False(new UserScheduleClient(Spa).Allows(caller));
    }

    [Theory]
    [InlineData("azp")]
    [InlineData("scp")]
    [InlineData("idtyp")]
    [InlineData("oid")]
    public void Duplicate_identity_claims_fail_closed(string claimType)
    {
        Assert.False(new UserScheduleClient(Spa).Allows(Caller(claims =>
            claims.Add(new(claimType, claims.Single(claim => claim.Type == claimType).Value)))));
    }

    [Fact]
    public void Missing_production_SPA_configuration_disables_management_without_affecting_other_API_authentication()
    {
        var production = new ConfigurationBuilder().Build();
        Assert.Equal(Guid.Empty, UserScheduleClient.FromConfiguration(production).ClientId);
        var development = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Authentication:Mode"] = "Development" }).Build();
        Assert.Equal(Guid.Parse(UserScheduleClient.DevelopmentClientId), UserScheduleClient.FromConfiguration(development).ClientId);
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AzureAd:SpaClientId"] = Spa.ToString("D") }).Build();
        Assert.Equal(Spa, UserScheduleClient.FromConfiguration(configured).ClientId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-client")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Invalid_configured_SPA_client_fails_startup_explicitly(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AzureAd:SpaClientId"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => UserScheduleClient.FromConfiguration(configuration));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public void Strong_ETags_round_trip_only_positive_decimal_configuration_versions(int expected)
    {
        var header = ScheduleEntityTag.Format(expected);
        Assert.True(ScheduleEntityTag.TryParse(header, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("1")]
    [InlineData("W/\"1\"")]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"01\"")]
    [InlineData("\"+1\"")]
    [InlineData("\" 1\"")]
    [InlineData("\"1 \"")]
    [InlineData("\"1\",\"2\"")]
    [InlineData("\"2147483648\"")]
    public void Weak_wildcard_ambiguous_or_invalid_ETags_cannot_bypass_optimistic_concurrency(string? header)
    {
        Assert.False(ScheduleEntityTag.TryParse(header, out _));
    }

    [Fact]
    public void Multiple_If_Match_headers_and_invalid_server_versions_fail()
    {
        Assert.False(ScheduleEntityTag.TryParse(new StringValues(["\"1\"", "\"2\""]), out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleEntityTag.Format(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleEntityTag.Format(-1));
    }

    [Fact]
    public void UI_request_uses_strict_defaults_without_becoming_a_public_SDK_trigger_contract()
    {
        var request = JsonSerializer.Deserialize(
            """{"workflowId":"demo/release/build","name":"nightly","timezone":"Etc/UTC","interval":"00:01:00"}""",
            UserScheduleJson.Default.UserScheduleRequest)!;
        Assert.Equal(ScheduleOverlapPolicy.Skip, request.Overlap);
        Assert.Equal(ScheduleCatchUpPolicy.Latest, request.CatchUp);
        Assert.Empty(request.Variables);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, UserScheduleJson.Default.UserScheduleRequest),
            UserScheduleJson.Default.UserScheduleRequest)!;
        Assert.Equal(request.Name, restored.Name);
        Assert.Equal(TimeSpan.FromMinutes(1), restored.Interval);
        Assert.Equal(typeof(UserSchedulesEndpoints).Assembly, typeof(UserScheduleRequest).Assembly);
        Assert.NotEqual(typeof(ScheduleInstance).Assembly, typeof(UserScheduleRequest).Assembly);
    }

    [Theory]
    [InlineData("""{"workflowId":"t/type/w","name":"n","timezone":"Etc/UTC","origin":"Code"}""")]
    [InlineData("""{"workflowId":"t/type/w","name":"n","timezone":"Etc/UTC","codeKey":"spoof"}""")]
    [InlineData("""{"workflowId":"t/type/w","name":"n","timezone":"Etc/UTC","rowVersion":1}""")]
    [InlineData("""{"workflowId":"t/type/w","name":"n","timezone":"Etc/UTC","paused":true}""")]
    [InlineData("""{"workflowId":"t/type/w","name":"n","timezone":"Etc/UTC","overlap":0}""")]
    [InlineData("""{"workflowId":"t/type/w","name":"n","timezone":"Etc/UTC","catchUp":"Every"}""")]
    [InlineData("""{"workflowId":"t/type/w","name":"n","timezone":"Etc/UTC","interval":"not-a-duration"}""")]
    [InlineData("""{"workflowId":"t/type/w","name":"n","name":"duplicate","timezone":"Etc/UTC"}""")]
    [InlineData("""{"workflowId":"t/type/w","name":null,"timezone":"Etc/UTC"}""")]
    [InlineData("""{"workflowId":"t/type/w","timezone":"Etc/UTC"}""")]
    [InlineData("[]")]
    public void UI_JSON_rejects_unknown_duplicate_missing_null_and_malformed_contract_properties(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, UserScheduleJson.Default.UserScheduleRequest));
    }

    [Theory]
    [InlineData("team/type/name", "team/type")]
    [InlineData("Team/Type/Name", "Team/Type")]
    [InlineData("\u65e5\u672c/\u7a2e\u985e/\u540d\u524d", "\u65e5\u672c/\u7a2e\u985e")]
    public void Shared_identity_resolution_retains_exact_team_and_type_names(string workflow, string scope)
    {
        Assert.True(WorkflowIdentity.TryGetTypeScope(workflow, out var actual));
        Assert.Equal(scope, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("team/type")]
    [InlineData("team/type/name/extra")]
    [InlineData("team//name")]
    [InlineData("team/type/..")]
    [InlineData("team/type/white\nline")]
    [InlineData("team/type/back\\slash")]
    public void Shared_identity_resolution_rejects_invalid_trust_boundary_inputs(string? workflow)
    {
        Assert.False(WorkflowIdentity.TryGetTypeScope(workflow, out _));
    }

    private static ClaimsPrincipal Caller(Action<List<Claim>>? change = null)
    {
        var claims = new List<Claim>
        {
            new("azp", Spa.ToString("D")), new("scp", "wf.read"),
            new("idtyp", "user"), new("oid", "44cd65e3-f210-4201-b5a8-74321260704a")
        };
        change?.Invoke(claims);
        return new(new ClaimsIdentity(claims, "test"));
    }
}
