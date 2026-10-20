using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests.Api;

public sealed class PublicApiContractTests
{
    private static readonly HashSet<(string Method, string Path)> ScheduleEditor =
    [
        ("POST", "/api/v1/schedules"), ("PUT", "/api/v1/schedules/{id}"), ("DELETE", "/api/v1/schedules/{id}"),
        ("POST", "/api/v1/schedules/{id}/pause"), ("POST", "/api/v1/schedules/{id}/resume"),
        ("POST", "/api/v1/schedules/preview")
    ];

    [Fact]
    public void PublicApiContract_every_UI_HTTP_action_maps_to_a_generated_public_operation_except_schedule_editor()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Frontend");
        var publicOperations = Operations(File.ReadAllText(Path.Combine(root, "lib", "api", "schema.d.ts")));
        var uiOperations = Operations(File.ReadAllText(Path.Combine(root, "lib", "api", "ui-schema.d.ts")));
        Assert.True(uiOperations.SetEquals(ScheduleEditor), "Only schedule-editor operations may be UI-only.");
        Assert.Empty(publicOperations.Intersect(ScheduleEditor));
        var observedPublic = new HashSet<(string, string)>();
        var observedUi = new HashSet<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".ts", StringComparison.Ordinal) || file.EndsWith(".tsx", StringComparison.Ordinal)) &&
                !file.EndsWith(".d.ts", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotMatch(@"\b(?:publicApi|uiApi)\s+as\b|=\s*(?:publicApi|uiApi)\b", source);
            Assert.DoesNotMatch(@"\bfetch\s*\(", source);
            var calls = Calls(source);
            foreach (var (client, method, path) in calls)
            {
                var operation = (method, path);
                Assert.True((client == "publicApi" ? publicOperations : uiOperations).Contains(operation),
                    $"{Path.GetRelativePath(root, file)}: undocumented {client} {method} {path}");
                if (client == "uiApi")
                {
                    Assert.Contains(operation, ScheduleEditor);
                    observedUi.Add(operation);
                }
                else observedPublic.Add(operation);
            }
        }
        Assert.True(observedUi.SetEquals(ScheduleEditor), "Every schedule-editor action must remain explicitly classified.");
        Assert.True(observedPublic.Count >= 20, "The scan must include all existing catalogue/run/report/version capabilities.");
        foreach (var operation in new[] { ("GET", "/api/v1/permissions/{scope}"), ("GET", "/api/v1/audit"),
            ("GET", "/api/v1/runs/{id}/system-events"),
            ("GET", "/api/v1/runs/{id}/tasks/{task}/{attempt}/logs"),
            ("GET", "/api/v1/series/{id}/trends"),
            ("GET", "/api/v1/runs/{id}/reports"), ("POST", "/api/v1/runs/{id}/rerun"),
            ("GET", "/api/v1/runs/{id}/clone"), ("POST", "/api/v1/runs/{id}/clone"),
            ("POST", "/api/v1/runs/placement-options"), ("POST", "/api/v1/runs/{id}/placement-options"),
            ("POST", "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/activate") })
            Assert.Contains(operation, observedPublic);
        foreach (var path in new[] { "/api/v1/schedules", "/api/v1/schedules/{id}",
            "/api/v1/series/{id}/fires", "/api/v1/series/{id}/history" })
            Assert.Contains(("GET", path), publicOperations);
        foreach (var method in new[] { "GET", "PUT" })
        {
            Assert.Contains((method, "/api/v1/admin/retention"), publicOperations);
            Assert.DoesNotContain((method, "/api/v1/admin/retention"), uiOperations);
        }
        foreach (var operation in new[] { ("GET", "/api/v1/subscriptions"), ("POST", "/api/v1/subscriptions"),
            ("DELETE", "/api/v1/subscriptions/{id}") })
        {
            Assert.Contains(operation, publicOperations);
            Assert.DoesNotContain(operation, uiOperations);
            Assert.Contains(operation, observedPublic);
        }
    }

    [Fact]
    public void PublicApiContract_scan_recognizes_conditional_paths_but_never_silently_skips_unclassified_dynamic_calls()
    {
        var calls = Calls("""
            publicApi.GET("/api/v1/runs", {});
            uiApi.POST(current.schedule.paused ? "/api/v1/schedules/{id}/resume" : "/api/v1/schedules/{id}/pause", {});
            """);
        Assert.Equal(3, calls.Count);
        Assert.ThrowsAny<Exception>(() => Calls("uiApi.POST(path, {})"));
        Assert.ThrowsAny<Exception>(() => Calls("publicApi.GET(`/api/v1/${value}`, {})"));
        var operations = Operations("""
            export interface paths {
                "/api/v1/runs": {
                    get: {
                        responses: { get: {}; };
                    };
                    post?: never;
                };
            }
            """);
        Assert.Equal([("GET", "/api/v1/runs")], operations);
    }

    [Fact]
    public void PublicApiContract_schedule_list_is_bounded_exact_scoped_and_cursor_bound_including_legacy_UUIDs()
    {
        var filters = new Dictionary<string, StringValues> { ["workflowId"] = "team/type/workflow", ["limit"] = "1" };
        var query = ScheduleListQuery.Parse(new QueryCollection(filters));
        var last = Guid.NewGuid();
        filters["cursor"] = query.Cursor(last);
        Assert.Equal(last, ScheduleListQuery.Parse(new QueryCollection(filters)).AfterId);
        filters["workflowId"] = "other/type/workflow";
        Assert.Throws<ArgumentException>(() => ScheduleListQuery.Parse(new QueryCollection(filters)));
        filters["workflowId"] = "team/type/workflow";
        filters["limit"] = "2";
        Assert.Throws<ArgumentException>(() => ScheduleListQuery.Parse(new QueryCollection(filters)));
    }

    [Theory]
    [InlineData("?type=release")]
    [InlineData("?team=team&team=other")]
    [InlineData("?team=bad/team")]
    [InlineData("?workflowId=bad")]
    [InlineData("?workflowId=team/type/workflow&limit=0")]
    [InlineData("?workflowId=team/type/workflow&limit=101")]
    [InlineData("?workflowId=team/type/workflow&limit=01")]
    [InlineData("?workflowId=team/type/workflow&workflowId=other/type/workflow")]
    [InlineData("?workflowId=team/type/workflow&status=Running")]
    [InlineData("?workflowId=team/type/workflow&cursor=invalid")]
    public void PublicApiContract_schedule_list_rejects_ambiguous_or_malformed_requests(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(query);
        Assert.Throws<ArgumentException>(() => ScheduleListQuery.Parse(context.Request.Query));
    }

    [Fact]
    public void PublicApiContract_global_schedule_cursor_binds_filters_limit_and_current_visible_types()
    {
        var context = new DefaultHttpContext();
        var all = ScheduleListQuery.Parse(context.Request.Query);
        Assert.Null(all.WorkflowId);
        Assert.Null(all.Filter.Team);
        Assert.Equal(50, all.Limit);
        context.Request.QueryString = new("?team=team&type=release&limit=1");
        var query = ScheduleListQuery.Parse(context.Request.Query);
        var last = Guid.NewGuid();
        var cursor = query.Cursor(last, ["team/release"]);
        context.Request.QueryString = new("?team=team&type=release&limit=1&cursor=" + cursor);
        var parsed = ScheduleListQuery.Parse(context.Request.Query);
        Assert.Equal(last, parsed.AfterId);
        Assert.Equal(parsed.Binding(["team/release"]), parsed.CursorBinding);
        Assert.NotEqual(parsed.Binding([]), parsed.CursorBinding);
        context.Request.QueryString = new("?team=team&type=release&limit=2&cursor=" + cursor);
        var resized = ScheduleListQuery.Parse(context.Request.Query);
        Assert.NotEqual(resized.Binding(["team/release"]), resized.CursorBinding);
        context.Request.QueryString = new("?workflowId=team/release/workflow&limit=1&cursor=" + cursor);
        Assert.Throws<ArgumentException>(() => ScheduleListQuery.Parse(context.Request.Query));
    }

    private static HashSet<(string Method, string Path)> Operations(string source)
    {
        var result = new HashSet<(string, string)>();
        foreach (Match path in Regex.Matches(source, """(?ms)^    "(?<path>/[^"]+)": \{(?<body>.*?)(?=^    "|^\})"""))
            foreach (Match method in Regex.Matches(path.Groups["body"].Value,
                @"(?m)^        (?<method>get|post|put|patch|delete|head|options): \{"))
                result.Add((method.Groups["method"].Value.ToUpperInvariant(), path.Groups["path"].Value));
        Assert.NotEmpty(result);
        return result;
    }

    private static List<(string Client, string Method, string Path)> Calls(string source)
    {
        const string prefix = @"\b(?<client>publicApi|uiApi)\.(?<method>GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s*\(";
        const string literal = "\"/api/v1/[^\"\\r\\n]+\"";
        var result = new List<(string, string, string)>();
        var matches = Regex.Matches(source, prefix + @"(?<argument>[^,)]+)(?:,|\))");
        Assert.Equal(Regex.Matches(source, prefix).Count, matches.Count);
        foreach (Match call in matches)
        {
            var argument = call.Groups["argument"].Value.Trim();
            Assert.Matches("^(?:" + literal + "|(?:[A-Za-z_$][\\w$]*\\.)*[A-Za-z_$][\\w$]*\\s*\\?\\s*" +
                literal + "\\s*:\\s*" + literal + ")$", argument);
            foreach (Match path in Regex.Matches(argument, literal))
                result.Add((call.Groups["client"].Value, call.Groups["method"].Value, path.Value[1..^1]));
        }
        return result;
    }
}
