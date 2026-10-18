using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class AuditApiTests
{
    [Fact]
    public void AuditApi_defaults_and_source_generated_contract_preserve_int64_ids_and_summary()
    {
        Assert.Equal(new AuditQuery(null, null, null, null, null, 50), AuditQuery.Parse(new QueryCollection()));
        using var summary = JsonDocument.Parse("""{"before":{"pool":"old"},"after":{"pool":"new"}}""");
        var page = new AuditPage([new("9223372036854775807", DateTimeOffset.UtcNow, "actor", "User", "machine.update",
            "Machine", "machine", null, summary.RootElement)], null);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(page, AuditJsonContext.Default.AuditPage));
        Assert.Equal("9223372036854775807", serialized.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("new", serialized.RootElement.GetProperty("items")[0].GetProperty("summary").GetProperty("after").GetProperty("pool").GetString());
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(AuditJsonContext.Default.AuditPage));
    }

    [Theory]
    [InlineData("unknown", "value")]
    [InlineData("Actor", "a")]
    [InlineData("actor", "")]
    [InlineData("targetId", " ")]
    [InlineData("targetKind", "Run\n")]
    [InlineData("from", "2026-10-17")]
    [InlineData("from", "2026-10-17T00:00:00")]
    [InlineData("to", "2026-02-30T00:00:00Z")]
    [InlineData("from", "2026-10-17T00:00:00.0000001Z")]
    [InlineData("limit", "0")]
    [InlineData("limit", "101")]
    [InlineData("limit", "01")]
    [InlineData("limit", "+1")]
    [InlineData("cursor", "not-a-cursor")]
    public void AuditApi_rejects_malformed_ambiguous_or_unbounded_filters(string key, string value) =>
        Assert.Throws<ArgumentException>(() => Parse((key, value)));

    [Fact]
    public void AuditApi_rejects_duplicates_overlong_text_and_empty_or_reversed_time_ranges()
    {
        foreach (var key in new[] { "actor", "targetKind", "targetId", "from", "to", "limit", "cursor" })
            Assert.Throws<ArgumentException>(() => AuditQuery.Parse(new QueryCollection(
                new Dictionary<string, StringValues> { [key] = new(["a", "b"]) })));
        foreach (var key in new[] { "actor", "targetKind", "targetId" })
            Assert.Throws<ArgumentException>(() => Parse((key, new string('a', 1025))));
        foreach (var end in new[] { "2026-10-17T00:00:00Z", "2026-10-16T00:00:00Z" })
            Assert.Throws<ArgumentException>(() => Parse(("from", "2026-10-17T00:00:00Z"), ("to", end)));
    }

    [Fact]
    public void AuditApi_keyset_is_microsecond_precise_and_bound_to_all_normalized_filters_and_limit()
    {
        (string, string)[] filters = [("actor", "actor"), ("targetKind", "Run"), ("targetId", "target"),
            ("from", "2026-10-17T01:00:00+01:00"), ("to", "2026-10-18T00:00:00Z"), ("limit", "2")];
        var query = Parse(filters);
        var position = new AuditPosition(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero).AddTicks(10), long.MaxValue);
        var cursor = query.Cursor(position);
        Assert.Equal(position, Parse([.. filters, ("cursor", cursor)]).After);
        Assert.Equal(position, Parse([.. filters.Select(pair => pair.Item1 == "from"
            ? ("from", "2026-10-17T00:00:00Z") : pair), ("cursor", cursor)]).After);
        foreach (var (key, _) in filters)
        {
            var changed = filters.Where(pair => pair.Item1 != key).Append(("cursor", cursor)).ToArray();
            Assert.Throws<ArgumentException>(() => Parse(changed));
        }
        Assert.Throws<ArgumentException>(() => Parse([.. filters, ("cursor", cursor + "=")]));
        Assert.Throws<ArgumentException>(() => Parse([.. filters, ("cursor", query.Cursor(position with { Id = 0 }))]));
        Assert.Throws<ArgumentException>(() => Parse([.. filters, ("cursor", query.Cursor(position with { At = position.At.AddTicks(1) }))]));
    }

    private static AuditQuery Parse(params (string Key, string Value)[] values) => AuditQuery.Parse(
        new QueryCollection(values.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value))));
}
