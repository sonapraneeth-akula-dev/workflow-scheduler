using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Wf.Contracts;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class RunQueryTests
{
    [Fact]
    public void RunQuery_defaults_and_safe_source_generated_schema()
    {
        var query = Parse();
        Assert.Equal(50, query.Limit);
        Assert.Null(query.AfterId);
        Assert.Empty(query.Metadata);
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(RunQueryJsonContext.Default.RunQueryPage));
        var json = JsonSerializer.Serialize(new RunQueryPage([], null), RunQueryJsonContext.Default.RunQueryPage);
        Assert.Equal("{\"items\":[],\"nextCursor\":null}", json);
    }

    [Fact]
    public void RunQuery_combines_exact_typed_filters_and_normalizes_time_offsets()
    {
        var series = Guid.NewGuid();
        var query = Parse(("team", "Team-🚀"), ("type", "release"), ("workflowId", "Team-🚀/release/Workflow"),
            ("seriesId", series.ToString()), ("status", "Succeeded"), ("from", "2026-10-15T11:00:00-07:00"),
            ("to", "2026-10-15T19:00:00.123456Z"), ("meta.environment", "prod"), ("meta.empty", ""), ("limit", "100"));
        Assert.Equal(series, query.SeriesId);
        Assert.Equal(RunStatus.Succeeded, query.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture), query.From);
        Assert.Equal("prod", query.Metadata["environment"]);
        Assert.Equal("", query.Metadata["empty"]);
        Assert.Equal(100, query.Limit);
    }

    [Theory]
    [InlineData("limit", "0")]
    [InlineData("limit", "101")]
    [InlineData("limit", "01")]
    [InlineData("limit", "+1")]
    [InlineData("limit", " 1")]
    [InlineData("limit", "999999999999999")]
    [InlineData("type", "release")]
    [InlineData("team", "..")]
    [InlineData("team", "team/type")]
    [InlineData("Team", "team")]
    [InlineData("status", "running")]
    [InlineData("status", "1")]
    [InlineData("status", "Running,Failed")]
    [InlineData("status", "Unknown")]
    [InlineData("seriesId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("seriesId", "01933FEA-1C00-7000-8000-000000000001")]
    [InlineData("workflowId", "team/type")]
    [InlineData("from", "2026-10-15")]
    [InlineData("from", "2026-10-15T00:00:00")]
    [InlineData("from", "2026-10-15T00:00:00.1234567Z")]
    [InlineData("from", "2026-02-30T00:00:00Z")]
    [InlineData("meta.", "value")]
    [InlineData("meta.key", "bad\nvalue")]
    [InlineData("unsupported", "ignored")]
    [InlineData("abortedBy", "principal")]
    [InlineData("cursor", "")]
    public void RunQuery_invalid_filters_are_not_ignored(string key, string value) =>
        Assert.Throws<ArgumentException>(() => Parse((key, value)));

    [Fact]
    public void RunQuery_rejects_duplicate_filters_inconsistent_scope_and_reversed_time()
    {
        Assert.Throws<ArgumentException>(() => RunQuery.Parse(new QueryCollection(new Dictionary<string, StringValues>
            { ["status"] = new(new[] { "Running", "Failed" }) })));
        Assert.Throws<ArgumentException>(() => Parse(("team", "team"), ("workflowId", "other/release/work")));
        Assert.Throws<ArgumentException>(() => Parse(("team", "team"), ("type", "release"), ("workflowId", "team/other/work")));
        Assert.Throws<ArgumentException>(() => Parse(("from", "2026-10-15T00:00:00Z"), ("to", "2026-10-15T00:00:00Z")));
        Assert.Throws<ArgumentException>(() => Parse(("meta.key", new string('x', 1025))));
        Assert.Throws<ArgumentException>(() => Parse(("meta." + new string('x', 129), "value")));
        Assert.Throws<ArgumentException>(() => Parse(Enumerable.Range(0, 33).Select(index => ("meta.k" + index, "v")).ToArray()));
    }

    [Fact]
    public void RunQuery_cursor_is_canonical_and_bound_to_every_filter_limit_and_current_scope()
    {
        var id = Guid.CreateVersion7();
        var original = Parse(("team", "team"), ("status", "Running"), ("meta.env", "prod"), ("limit", "2"));
        var scopes = new[] { "team/release", "team/build" };
        var cursor = original.Cursor(id, scopes);
        var next = Parse(("team", "team"), ("status", "Running"), ("meta.env", "prod"), ("limit", "2"), ("cursor", cursor));
        Assert.Equal(138, cursor.Length);
        Assert.Equal(id, next.AfterId);
        Assert.Equal(original.Binding(scopes.Reverse()), next.CursorBinding);
        foreach (var changed in new[]
        {
            Parse(("team", "other"), ("status", "Running"), ("meta.env", "prod"), ("limit", "2")),
            Parse(("team", "team"), ("status", "Failed"), ("meta.env", "prod"), ("limit", "2")),
            Parse(("team", "team"), ("status", "Running"), ("meta.env", "dev"), ("limit", "2")),
            Parse(("team", "team"), ("status", "Running"), ("meta.env", "prod"), ("limit", "3")),
            Parse(("team", "team"), ("status", "Running"), ("meta.env", "prod"), ("limit", "2"), ("from", "2026-10-15T00:00:00Z"))
        }) Assert.NotEqual(next.CursorBinding, changed.Binding(scopes));
        Assert.NotEqual(next.CursorBinding, original.Binding(["team/release"]));
        var reversedMetadata = Parse(("meta.b", "b"), ("meta.a", "a"));
        Assert.Equal(Parse(("meta.a", "a"), ("meta.b", "b")).Binding(scopes), reversedMetadata.Binding(scopes));
    }

    [Fact]
    public void RunQuery_cursor_rejects_padding_invalid_utf8_version_and_noncanonical_uuid()
    {
        var valid = Parse().Cursor(Guid.CreateVersion7(), []);
        var decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(valid));
        foreach (var cursor in new[]
        {
            valid + "=", new string('x', 139), WebEncoders.Base64UrlEncode([0xff]),
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("2" + decoded[1..])),
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded.ToUpperInvariant())),
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("1\n" + Guid.NewGuid() + "\n" + new string('a', 64)))
        }) Assert.Throws<ArgumentException>(() => Parse(("cursor", cursor)));
    }

    private static RunQuery Parse(params (string Key, string Value)[] values) =>
        RunQuery.Parse(new QueryCollection(values.ToDictionary(value => value.Key, value => new StringValues(value.Value), StringComparer.Ordinal)));
}
