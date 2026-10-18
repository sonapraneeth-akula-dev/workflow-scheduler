using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class SystemEventsTests
{
    [Fact]
    public void SystemEvents_cursor_preserves_Int64_and_binds_run_filters_page_size_and_watermark()
    {
        var run = Guid.CreateVersion7();
        const string filters = "?node=build&task=build%2Fcompile&attempt=2&limit=1";
        var query = SystemEventsQuery.Parse(run, Query(filters));
        var cursor = query.Cursor(run, long.MaxValue - 2, long.MaxValue);
        var restored = SystemEventsQuery.Parse(run, Query(filters + "&cursor=" + cursor));
        Assert.Equal(long.MaxValue - 2, restored.AfterSeq);
        Assert.Equal(long.MaxValue, restored.ThroughSeq);
        Assert.Equal(query with { AfterSeq = restored.AfterSeq, ThroughSeq = restored.ThroughSeq }, restored);
        foreach (var changed in new[] { filters.Replace("build", "other", StringComparison.Ordinal),
            filters.Replace("attempt=2", "attempt=3", StringComparison.Ordinal),
            filters.Replace("limit=1", "limit=2", StringComparison.Ordinal), "?limit=1" })
            Assert.Throws<ArgumentException>(() => SystemEventsQuery.Parse(run, Query(changed + "&cursor=" + cursor)));
        Assert.Throws<ArgumentException>(() => SystemEventsQuery.Parse(Guid.CreateVersion7(), Query(filters + "&cursor=" + cursor)));
        Assert.Throws<ArgumentException>(() => SystemEventsQuery.Parse(run,
            Query(filters + "&cursor=" + query.Cursor(run, 2, 2))));
        var shortCursor = query.Cursor(run, 1, 2);
        Assert.Equal(95, shortCursor.Length);
        Assert.Equal(1, SystemEventsQuery.Parse(run, Query(filters + "&cursor=" + shortCursor)).AfterSeq);
    }

    [Theory]
    [InlineData("?unknown=value")]
    [InlineData("?Limit=1")]
    [InlineData("?limit=1&limit=1")]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?limit=01")]
    [InlineData("?attempt=0")]
    [InlineData("?attempt=32768")]
    [InlineData("?attempt=-1")]
    [InlineData("?attempt=1.0")]
    [InlineData("?attempt=1e2")]
    [InlineData("?node=")]
    [InlineData("?node=a%2Fb")]
    [InlineData("?node=a%0Ab")]
    [InlineData("?task=task")]
    [InlineData("?task=node%2Ftask%2Fextra")]
    [InlineData("?task=node%2F")]
    [InlineData("?node=other&task=node%2Ftask")]
    [InlineData("?cursor=bad")]
    public void SystemEvents_rejects_ambiguous_unbounded_or_noncanonical_queries(string query) =>
        Assert.Throws<ArgumentException>(() => SystemEventsQuery.Parse(Guid.CreateVersion7(), Query(query)));

    [Fact]
    public void SystemEvents_empty_filters_are_valid_and_UTF8_key_boundary_is_exact()
    {
        var run = Guid.CreateVersion7();
        Assert.Equal(new(null, null, null, 100), SystemEventsQuery.Parse(run, Query("")));
        var key = new string('\u03b4', 256);
        Assert.Equal(key, SystemEventsQuery.Parse(run, Query("?node=" + Uri.EscapeDataString(key))).NodeKey);
        Assert.Throws<ArgumentException>(() =>
            SystemEventsQuery.Parse(run, Query("?node=" + Uri.EscapeDataString(key + "a"))));
    }

    [Fact]
    public void SystemEvents_projection_excludes_freeform_failure_abort_source_and_operation_secrets()
    {
        var change = new RunEvent(Guid.CreateVersion7(), long.MaxValue, DateTimeOffset.UtcNow,
            EventEntity.Run, null, (int)RunStatus.Failed)
        { FailureCategory = FailureCategory.PlatformError, FailureReason = "private-operation-url-and-token" };
        var page = new SystemEventsPage(change.RunId, "team/type/workflow", Guid.CreateVersion7(),
            long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), [SystemEvent.From(change)], null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, SystemEventsJson.Default.SystemEventsPage));
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            json.RootElement.GetProperty("events")[0].GetProperty("seq").GetString());
        Assert.DoesNotContain("private-", json.RootElement.GetRawText());
        Assert.Equal(new[] { "agentId", "at", "attempt", "entity", "failureCategory", "key", "plannedAgentId",
            "progress", "selectionReason", "seq", "state", "waitingReason" },
            json.RootElement.GetProperty("events")[0].EnumerateObject().Select(property => property.Name).Order());
        Assert.Null(SystemEvent.From(change).SelectionReason);
        Assert.Throws<ArgumentException>(() => SystemEvent.From(change with { SelectionReason = "private-secret" }));
    }

    private static IQueryCollection Query(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(query);
        return context.Request.Query;
    }
}
