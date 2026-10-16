using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Tests;

public sealed class RunEventsResyncTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("9007199254740991", 9007199254740991L)]
    [InlineData("9007199254740992", 9007199254740992L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Query_preserves_canonical_nonnegative_Int64_exactly(string text, long expected) =>
        Assert.Equal(expected, RunEventsQuery.AfterSequence(Query("?afterSeq=" + text)));

    [Theory]
    [InlineData("")]
    [InlineData("?afterSeq=")]
    [InlineData("?afterSeq=-1")]
    [InlineData("?afterSeq=-0")]
    [InlineData("?afterSeq=01")]
    [InlineData("?afterSeq=%2B1")]
    [InlineData("?afterSeq=%201")]
    [InlineData("?afterSeq=1%20")]
    [InlineData("?afterSeq=1.0")]
    [InlineData("?afterSeq=1e2")]
    [InlineData("?afterSeq=NaN")]
    [InlineData("?afterSeq=9223372036854775808")]
    [InlineData("?afterSeq=0&afterSeq=0")]
    [InlineData("?afterSeq=0&AfterSeq=1")]
    [InlineData("?AfterSeq=0")]
    [InlineData("?afterSeq=0&limit=1")]
    [InlineData("?afterSeq=0&cursor=x")]
    public void Query_rejects_missing_duplicate_unknown_and_noncanonical_numbers(string query) =>
        Assert.Throws<ArgumentException>(() => RunEventsQuery.AfterSequence(Query(query)));

    [Fact]
    public void Threshold_is_exactly_5000_with_no_Int64_overflow()
    {
        Assert.False(RunEventsQuery.RequiresSnapshot(0, 0));
        Assert.False(RunEventsQuery.RequiresSnapshot(0, 5000));
        Assert.True(RunEventsQuery.RequiresSnapshot(0, 5001));
        Assert.False(RunEventsQuery.RequiresSnapshot(long.MaxValue - 5000, long.MaxValue));
        Assert.True(RunEventsQuery.RequiresSnapshot(long.MaxValue - 5001, long.MaxValue));
        Assert.False(RunEventsQuery.RequiresSnapshot(long.MaxValue, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunEventsQuery.RequiresSnapshot(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunEventsQuery.RequiresSnapshot(1, 0));
    }

    [Fact]
    public void Resync_response_returns_200_for_5000_and_409_for_5001_without_truncation_or_overflow()
    {
        var run = Guid.CreateVersion7();
        var events = Enumerable.Range(1, 5001).Select(sequence =>
            new LiveEvent(sequence, "2026-10-15T00:00:00.0000000+00:00", 2, "node/task", 3, null)).ToArray();
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, 0, 5000, events[..5000])).StatusCode);
        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, 0, 5001, events)).StatusCode);
        Assert.Equal(500, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, 0, 5001, events[..5000])).StatusCode);
        Assert.Equal(500, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, 0, 5000, events)).StatusCode);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, long.MaxValue, long.MaxValue, [])).StatusCode);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, long.MaxValue - 1, long.MaxValue, [events[0] with { Seq = long.MaxValue }])).StatusCode);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, 1, 0, [])).StatusCode);
        Assert.Equal(500, Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            RunEventsEndpoints.ResyncResponse(run, 0, 2, [events[0], events[0]])).StatusCode);
    }

    [Fact]
    public void Identity_requires_the_same_canonical_UUIDv7_as_run_logs()
    {
        var id = Guid.CreateVersion7();
        Assert.Equal(id, RunEventsQuery.RunId(id.ToString("D")));
        foreach (var invalid in new[] { "", "invalid", Guid.Empty.ToString(), Guid.NewGuid().ToString(),
            id.ToString("N"), id.ToString("B"), " " + id.ToString("D"), id.ToString("D").ToUpperInvariant() })
            Assert.Throws<ArgumentException>(() => RunEventsQuery.RunId(invalid));
    }

    [Fact]
    public void Events_use_the_exact_live_projection_and_camelcase_source_generated_contract()
    {
        var at = new DateTimeOffset(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);
        var abort = new AbortRecord("actor", "Display name", at, "Requested reason", AbortSource.API);
        var change = new RunEvent(Guid.CreateVersion7(), long.MaxValue, at, EventEntity.Run, null, 4, abort);
        var page = new RunEventsPage(change.RunId, long.MaxValue, [LiveEvent.From(change)]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, RunEventsJsonContext.Default.RunEventsPage));
        Assert.Equal(long.MaxValue, json.RootElement.GetProperty("lastSeq").GetInt64());
        var item = json.RootElement.GetProperty("events")[0];
        Assert.Equal(new[] { "abort", "at", "entity", "key", "seq", "state" },
            item.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(at.ToString("O", CultureInfo.InvariantCulture), item.GetProperty("at").GetString());
        Assert.Equal(long.MaxValue, item.GetProperty("seq").GetInt64());
        Assert.Equal("API", item.GetProperty("abort").GetProperty("source").GetString());
        Assert.Equal("Requested reason", item.GetProperty("abort").GetProperty("reason").GetString());
    }

    [Fact]
    public void Snapshot_contract_exposes_only_identity_state_sequence_and_abort()
    {
        var response = new RunSnapshotResponse(Guid.CreateVersion7(), "team/type/workflow", Guid.CreateVersion7(),
            null, 0, 0, ImmutableDictionary<string, int>.Empty, ImmutableDictionary<string, int>.Empty, null, null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, RunEventsJsonContext.Default.RunSnapshotResponse));
        Assert.Equal(new[] { "abort", "endedAt", "lastSeq", "nodes", "runId", "seriesId", "status", "tasks", "versionId", "workflowId" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order());
    }

    private static IQueryCollection Query(string text)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(text);
        return context.Request.Query;
    }
}
