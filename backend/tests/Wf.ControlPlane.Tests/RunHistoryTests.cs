using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class RunHistoryTests
{
    private static RunHistoryQuery Parse(params (string Key, string Value)[] values) =>
        RunHistoryQuery.Parse(new QueryCollection(values.ToDictionary(pair => pair.Key,
            pair => new StringValues(pair.Value))), "team/type/workflow");

    [Fact]
    public void RunHistory_defaults_reuse_run_filters_and_source_generated_contract_has_required_arrays()
    {
        var filter = Parse();
        Assert.Equal("team/type/workflow", filter.Runs.WorkflowId);
        Assert.Equal("createdAtDesc", filter.Sort);
        Assert.Equal(50, filter.Runs.Limit);
        Assert.Null(filter.After);
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(RunHistoryJsonContext.Default.RunHistoryPage));
        Assert.Equal("{\"items\":[],\"nextCursor\":null,\"currentScheduleId\":null}",
            JsonSerializer.Serialize(new RunHistoryPage([], null), RunHistoryJsonContext.Default.RunHistoryPage));
    }

    [Theory]
    [InlineData("sort", "duration")]
    [InlineData("sort", "desc")]
    [InlineData("status", "aborted")]
    [InlineData("outcome", "Started")]
    [InlineData("abortedBy", "name")]
    [InlineData("abortedBy", "00000000-0000-0000-0000-000000000000")]
    [InlineData("abortedBy", "EF8A05A0-451C-4479-9C8D-391B6BC7EE1A")]
    [InlineData("limit", "01")]
    [InlineData("cursor", "")]
    [InlineData("cursor", "not-a-cursor")]
    [InlineData("unknown", "ignored")]
    [InlineData("workflowId", "team/type/override")]
    public void RunHistory_rejects_invalid_or_overridden_filters(string key, string value) =>
        Assert.Throws<ArgumentException>(() => Parse((key, value)));

    [Fact]
    public void RunHistory_requires_resource_and_rejects_duplicates_and_noncanonical_cursor()
    {
        Assert.Throws<ArgumentException>(() => RunHistoryQuery.Parse(QueryCollection.Empty));
        Assert.Throws<ArgumentException>(() => RunHistoryQuery.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { ["sort"] = new(new[] { "createdAtAsc", "createdAtDesc" }) }), "team/type/workflow"));
        var filter = Parse();
        var cursor = filter.Cursor(new(DateTimeOffset.UnixEpoch, Guid.CreateVersion7(), 0), ["team/type"]);
        foreach (var text in new[]
        {
            "h1\n1\n" + Guid.CreateVersion7() + "\n0\n" + new string('a', 64),
            "h1\n0\n" + Guid.Empty + "\n0\n" + new string('a', 64),
            "h1\n0\n" + Guid.NewGuid() + "\n0\n" + new string('a', 64),
            "h1\n0\n" + Guid.CreateVersion7() + "\n2\n" + new string('a', 64)
        })
            Assert.Throws<ArgumentException>(() => Parse(("cursor", WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(text)))));
        Assert.Throws<ArgumentException>(() => Parse(("cursor", cursor + "=")));
    }

    [Fact]
    public void RunHistory_cursor_binds_every_history_filter_and_fresh_scope()
    {
        var actor = Guid.NewGuid().ToString();
        var filter = Parse(("status", "Aborted"), ("abortedBy", actor), ("sort", "createdAtAsc"), ("limit", "2"));
        var position = new RunHistoryPosition(DateTimeOffset.UnixEpoch.AddTicks(10), Guid.CreateVersion7(), 0);
        var parsed = Parse(("status", "Aborted"), ("abortedBy", actor), ("sort", "createdAtAsc"), ("limit", "2"),
            ("cursor", filter.Cursor(position, ["team/type"])));
        Assert.Equal(position, parsed.After);
        Assert.Equal(filter.Binding(["team/type"]), parsed.CursorBinding);
        foreach (var other in new[]
        {
            Parse(("status", "Aborted"), ("abortedBy", actor), ("limit", "2")),
            Parse(("status", "Aborted"), ("sort", "createdAtAsc"), ("limit", "2")),
            Parse(("status", "Aborted"), ("abortedBy", actor), ("sort", "createdAtAsc"), ("limit", "3")),
            Parse(("status", "Aborted"), ("abortedBy", actor), ("sort", "createdAtAsc"), ("limit", "2"), ("outcome", "Run"))
        })
            Assert.NotEqual(parsed.CursorBinding, other.Binding(["team/type"]));
        Assert.NotEqual(parsed.CursorBinding, filter.Binding([]));
    }

    [Fact]
    public void RunHistory_variables_project_schema_only_and_metadata_reads_PascalCase_copied_strings()
    {
        var variables = RunHistoryQuery.Variables("""[{"key":"token","type":"Secret"},{"key":"count","type":"Int"}]""");
        Assert.Equal(["count", "token"], variables.Select(value => value.Key));
        Assert.True(variables[1].Redacted);
        Assert.False(variables[0].Redacted);
        Assert.Throws<InvalidDataException>(() => RunHistoryQuery.Variables("""[{"key":"x","type":"0"}]"""));
        Assert.Null(RunMetadata.FromStored("{}"));
        Assert.Null(RunMetadata.FromStored("""{"Type":null,"Values":{"private":"ignored"}}"""));
        Assert.Null(RunMetadata.FromStored("""{"type":"current","values":{}}"""));
        Assert.Empty(RunMetadata.FromStored("""{"Type":"release","Values":null}""")!.Values);
        var metadata = RunMetadata.FromStored("""{"Type":"release","Values":{"env":"old"},"private":"blob://not-returned"}""")!;
        Assert.Equal("old", metadata.Values["env"]);
        Assert.Throws<InvalidDataException>(() => RunMetadata.FromStored("""{"Type":"release","Values":{"x":4}}"""));
    }
}
