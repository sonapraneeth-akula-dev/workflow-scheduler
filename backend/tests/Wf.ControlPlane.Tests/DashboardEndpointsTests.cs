using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api.Dashboards;

namespace Wf.ControlPlane.Tests;

public sealed class DashboardEndpointsTests
{
    [Theory]
    [InlineData("workflow", "team/type/workflow", null)]
    [InlineData("node", "team/type/workflow/node", "node")]
    [InlineData("node", "a/b/c/caf\u00e9", "caf\u00e9")]
    public void Exact_scoped_identities_use_canonical_UTF8_base64url(string scope, string identity, string? node)
    {
        var query = DashboardQuery.Parse(scope, DashboardQuery.Encode(identity), QueryCollection.Empty, DateTimeOffset.UtcNow);
        Assert.Equal("team/type/workflow" == identity ? identity : string.Join('/', identity.Split('/').Take(3)), query.WorkflowId);
        Assert.Equal(node, query.NodeKey);
        Assert.Equal(10, query.Top);
        Assert.Equal("30d", query.Window.Name);
    }

    [Theory]
    [InlineData("workflow", "team/type")]
    [InlineData("workflow", "team/type/workflow/extra")]
    [InlineData("node", "team/type/workflow")]
    [InlineData("node", "team/type/workflow/..")]
    [InlineData("node", "team/type/workflow/a\\b")]
    [InlineData("node", "team/type/workflow/\n")]
    public void Incomplete_ambiguous_and_unsafe_scopes_fail(string scope, string identity) =>
        Assert.Throws<ArgumentException>(() => DashboardQuery.Parse(scope, DashboardQuery.Encode(identity), QueryCollection.Empty,
            DateTimeOffset.UtcNow));

    [Theory]
    [InlineData("machine", "00000000-0000-0000-0000-000000000000")]
    [InlineData("schedule", "not-a-uuid")]
    [InlineData("workflow", "Zg==")]
    [InlineData("workflow", "_w")]
    [InlineData("unknown", "id")]
    public void Noncanonical_ids_fail(string scope, string id) =>
        Assert.Throws<ArgumentException>(() => DashboardQuery.Parse(scope, id, QueryCollection.Empty, DateTimeOffset.UtcNow));

    [Theory]
    [InlineData("top", "0")]
    [InlineData("top", "51")]
    [InlineData("top", "01")]
    [InlineData("top", "+1")]
    [InlineData("top", " 1")]
    [InlineData("Top", "1")]
    [InlineData("window", "7D")]
    [InlineData("secret", "private")]
    public void Invalid_filters_are_not_silently_ignored(string key, string value) =>
        Assert.Throws<ArgumentException>(() => DashboardQuery.Parse("workflow", DashboardQuery.Encode("t/y/w"),
            new QueryCollection(new Dictionary<string, StringValues> { [key] = value }), DateTimeOffset.UtcNow));

    [Fact]
    public void Windows_top_and_duplicate_validation_share_strict_trend_semantics()
    {
        var query = DashboardQuery.Parse("machine", Guid.NewGuid().ToString(), new QueryCollection(new Dictionary<string, StringValues>
            { ["top"] = "50", ["window"] = "90d" }), new(2026, 10, 17, 1, 0, 0, TimeSpan.FromHours(5.5)));
        Assert.Equal(50, query.Top);
        Assert.Equal(new DateOnly(2026, 10, 17), query.Window.To);
        Assert.Equal(90, query.Window.Days);
        Assert.Throws<ArgumentException>(() => DashboardQuery.Parse("machine", query.Id, new QueryCollection(
            new Dictionary<string, StringValues> { ["top"] = new(["1", "2"]) }), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Rankings_are_descending_stable_bounded_and_exclude_unmeasured_values_not_real_zero()
    {
        DashboardRow Row(string workflow, string node, double? duration, double rate, double? cpu, decimal? ram) =>
            new(workflow, node, 10, (long)(rate * 10), 0, duration is null ? 0 : 10, rate, duration, duration, cpu, ram,
                Guid.Empty, "/runs/test");
        var slow = Row("t/y/w", "slow", 100, 0.1, null, 0);
        var hot = Row("t/y/w", "hot", 10, 0.8, 150, 10);
        var first = Row("t/y/a", "same", 10, 0.8, 150, 10);
        var missing = Row("t/y/w", "unknown", null, 0, null, null);
        var ranked = DashboardQuery.Rank([slow, hot, missing, first], 2);
        Assert.Equal([slow, first], ranked.Slowest);
        Assert.Equal([first, hot], ranked.FailureRate);
        Assert.Equal([first, hot], ranked.Cpu);
        Assert.Equal([first, hot], ranked.Ram);
        var full = DashboardQuery.Rank([missing, slow], 10);
        Assert.Empty(full.Cpu);
        Assert.Equal([slow], full.Ram);
        Assert.Equal([slow], full.Slowest);
        Assert.Equal(2, full.FailureRate.Length);
    }

    [Fact]
    public void Generated_response_projects_only_public_metrics_and_preserves_nulls()
    {
        var row = new DashboardRow("t/y/w", "n", 1, 0, 0, 0, 0, null, null, null, null, Guid.Empty, "/runs/id");
        var response = new DashboardResponse("workflow", "id", "7d", new(2026, 10, 11), new(2026, 10, 18), 10,
            "Node", new([], [row], [], []));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, DashboardJson.Default.DashboardResponse));
        var item = json.RootElement.GetProperty("rankings").GetProperty("failureRate")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("p95Ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("ramPeakMb").ValueKind);
        Assert.Equal(0, item.GetProperty("failureRate").GetDouble());
        foreach (var field in new[] { "metadata", "variables", "error", "outputs", "agentName", "packageUri" })
            Assert.False(item.TryGetProperty(field, out _));
    }
}
