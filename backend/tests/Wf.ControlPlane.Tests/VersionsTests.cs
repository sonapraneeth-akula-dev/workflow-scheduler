using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class VersionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionActivation_source_generated_response_exposes_only_workflow_and_exact_active_latest_UUIDs(bool hasLatest)
    {
        var active = Guid.CreateVersion7();
        Guid? latest = hasLatest ? Guid.CreateVersion7() : null;
        var response = new WorkflowVersionActivation("team/type/workflow", active, latest);
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(response, VersionsJsonContext.Default.WorkflowVersionActivation);
        using var json = JsonDocument.Parse(utf8);
        Assert.Equal(["activeVersionId", "latestVersionId", "workflowId"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(active, json.RootElement.GetProperty("activeVersionId").GetGuid());
        Assert.Equal(latest, hasLatest ? json.RootElement.GetProperty("latestVersionId").GetGuid() : null);
        if (!hasLatest) Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("latestVersionId").ValueKind);
        Assert.Equal(response, JsonSerializer.Deserialize(utf8, VersionsJsonContext.Default.WorkflowVersionActivation));
    }

    [Fact]
    public void Versions_defaults_and_source_generated_provenance_are_explicit_and_safe()
    {
        var filter = Query();
        Assert.Equal(50, filter.Limit);
        Assert.Null(filter.After);
        var registration = new WorkflowVersionRegistration(new('a', 40), "Git author", "Registrar", null,
            "1.2.3", new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var id = Guid.CreateVersion7();
        var page = new WorkflowVersionPage([new(id, filter.WorkflowId, "2.0.0-rc.1+build", registration)], null, id, id);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, VersionsJsonContext.Default.WorkflowVersionPage));
        Assert.Equal(["activeVersionId", "items", "latestVersionId", "nextCursor"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order());
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(["registration", "version", "versionId", "workflowId"], item.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(["changeNote", "gitAuthor", "gitCommit", "registeredAt", "registeredBy", "sdkVersion"],
            item.GetProperty("registration").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("registration").GetProperty("changeNote").ValueKind);
        Assert.Equal("2.0.0-rc.1+build", item.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("limit", "0")]
    [InlineData("limit", "101")]
    [InlineData("limit", "01")]
    [InlineData("limit", "+1")]
    [InlineData("limit", " 1")]
    [InlineData("limit", "1.0")]
    [InlineData("limit", "")]
    [InlineData("limit", "99999999999999")]
    [InlineData("Limit", "1")]
    [InlineData("after", "ignored")]
    [InlineData("cursor", "")]
    public void Versions_invalid_filters_fail_explicitly(string key, string value) =>
        Assert.Throws<ArgumentException>(() => Query((key, value)));

    [Fact]
    public void Versions_reject_duplicate_filters_and_invalid_workflow_identity()
    {
        Assert.Throws<ArgumentException>(() => WorkflowVersionsQuery.Parse("team/type/workflow",
            new QueryCollection(new Dictionary<string, StringValues> { ["limit"] = new(["1", "2"]) })));
        Assert.Throws<ArgumentException>(() => WorkflowVersionsQuery.Parse("team/type/workflow",
            new QueryCollection(new Dictionary<string, StringValues> { ["cursor"] = new(["a", "b"]) })));
        foreach (var identity in new[] { "team/type", "team/type/..", "team/type/w\nf", "team/type/a\\b" })
            Assert.Throws<ArgumentException>(() => WorkflowVersionsQuery.Parse(identity, QueryCollection.Empty));
    }

    [Fact]
    public void Versions_cursor_preserves_microseconds_and_binds_exact_workflow_and_limit()
    {
        var filter = Query(("limit", "2"));
        var at = DateTimeOffset.Parse("2026-10-16T12:34:56.123456Z", CultureInfo.InvariantCulture);
        var id = Guid.CreateVersion7();
        var cursor = filter.Cursor(at, id);
        var parsed = Query(("limit", "2"), ("cursor", cursor));
        Assert.Equal(new(at, id), parsed.After);
        Assert.Equal(cursor, parsed.Cursor(at.ToOffset(TimeSpan.FromHours(5)), id));
        Assert.Throws<ArgumentException>(() => Query(("limit", "3"), ("cursor", cursor)));
        Assert.Throws<ArgumentException>(() => WorkflowVersionsQuery.Parse("team/type/other",
            new QueryCollection(new Dictionary<string, StringValues> { ["limit"] = "2", ["cursor"] = cursor })));
        Assert.Null(Query(("limit", "1")).After);
        Assert.Equal(100, Query(("limit", "100")).Limit);
    }

    [Fact]
    public void Versions_cursor_rejects_noncanonical_time_uuid_encoding_and_payloads()
    {
        var filter = Query();
        var valid = filter.Cursor(new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), Guid.CreateVersion7());
        var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(valid)).Split('\n');
        var invalid = new List<string>
        {
            valid + "=", new('a', 257), WebEncoders.Base64UrlEncode(Enumerable.Repeat((byte)0xff, 100).ToArray())
        };
        foreach (var (index, value) in new (int, string)[]
        {
            (0, "v2"), (1, "-1"), (1, "0" + parts[1]), (1, "3155378976000000000"),
            (1, (long.Parse(parts[1], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)),
            (2, Guid.Empty.ToString()), (2, Guid.NewGuid().ToString()), (2, parts[2].ToUpperInvariant()),
            (3, new string('a', 64))
        })
        {
            var changed = parts.ToArray();
            changed[index] = value;
            invalid.Add(WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(string.Join('\n', changed))));
        }
        Assert.All(invalid, cursor => Assert.Throws<ArgumentException>(() => Query(("cursor", cursor))));
    }

    [Fact]
    public void Task_plan_is_an_explicit_safe_projection_with_dependencies_policies_and_declared_actions()
    {
        var machine = Guid.NewGuid();
        var placement = new PlacementPolicy(new(pool: new(variable: "pool"), machineIds: [machine], count: 1));
        var policy = new ExecutionPolicy(TimeSpan.FromSeconds(12), new(2,
            new(BackoffKind.Exponential, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8)),
            [FailureCategory.AgentLost]), false, new(0.25, 192));
        var task = new TaskDefinition("execute", policy: policy, placement: placement,
            action: new(TaskActionKind.SdkFunction, "Declared.Workflow.Execute"));
        var manifest = new WorkflowManifest("1", "team/type/plan", "1.2.3",
            new("type", ImmutableSortedDictionary<string, string>.Empty.Add("private", "metadata-secret")),
            [new("password", VariableType.String, @default: JsonSerializer.SerializeToElement("default-secret"))],
            ["$init"], [new("work", [new("$init", DependencyCondition.Always)], [task],
                inputs: [new("$init", "private-input")], timeout: TimeSpan.FromMinutes(1), placement: placement)],
            TimeSpan.FromHours(1), placement);
        var plan = WorkflowVersionPlan.From(Guid.CreateVersion7(), manifest);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan, VersionsJsonContext.Default.WorkflowVersionPlan));
        var root = json.RootElement;
        Assert.Equal(new[] { "nodes", "placement", "runTimeout", "stages", "version", "versionId", "workflowId" },
            root.EnumerateObject().Select(property => property.Name).Order());
        var node = Assert.Single(plan.Nodes);
        Assert.Equal(new("$init", DependencyCondition.Always), Assert.Single(node.Dependencies));
        Assert.Equal(TimeSpan.FromMinutes(1), node.Timeout);
        Assert.Equal(policy.AttemptTimeout, Assert.Single(node.Tasks).Policy.AttemptTimeout);
        Assert.Equal(policy.Retry.Retries, node.Tasks[0].Policy.Retry.Retries);
        Assert.Equal(policy.Limits, node.Tasks[0].Policy.Limits);
        Assert.Equal(192UL, json.RootElement.GetProperty("nodes")[0].GetProperty("tasks")[0]
            .GetProperty("policy").GetProperty("limits").GetProperty("ramMb").GetUInt64());
        Assert.Equal(policy.Retry.On, node.Tasks[0].Policy.Retry.On);
        Assert.False(node.Tasks[0].Policy.AutoRetry);
        Assert.Equal("pool", node.Tasks[0].Placement!.Selector!.Pool!.Variable);
        Assert.Equal([machine], node.Tasks[0].Placement!.Selector!.MachineIds);
        Assert.Equal(1, node.Tasks[0].Placement!.Selector!.Count);
        Assert.Equal(machine, root.GetProperty("placement").GetProperty("selector").GetProperty("machineIds")[0].GetGuid());
        Assert.Equal(1, root.GetProperty("placement").GetProperty("selector").GetProperty("count").GetInt32());
        Assert.Equal("SdkFunction", root.GetProperty("nodes")[0].GetProperty("tasks")[0].GetProperty("action").GetProperty("kind").GetString());
        foreach (var value in new[] { "metadata-secret", "default-secret", "private-input", "\"variables\"", "\"inputs\"" })
            Assert.DoesNotContain(value, root.GetRawText(), StringComparison.Ordinal);
    }

    private static WorkflowVersionsQuery Query(params (string Key, string Value)[] values) =>
        WorkflowVersionsQuery.Parse("team/type/workflow", new QueryCollection(values.ToDictionary(
            value => value.Key, value => new StringValues(value.Value), StringComparer.Ordinal)));
}
