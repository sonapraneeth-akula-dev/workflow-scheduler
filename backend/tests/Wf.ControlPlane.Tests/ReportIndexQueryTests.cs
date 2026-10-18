using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Tests;

public sealed class ReportIndexQueryTests
{
    [Theory]
    [InlineData("kind")]
    [InlineData("tag")]
    public void ReportIndex_filters_reject_duplicates_empty_blank_oversized_invalid_UTF8_and_NUL(string name)
    {
        foreach (var values in new StringValues[]
        {
            new(new[] { "text", "text" }), new(new[] { "a", "b" }), "", " ", "\t",
            new string('a', 129), new string('é', 65), "\ud800", "nul\0tag"
        })
            Assert.ThrowsAny<ArgumentException>(() => ReportIndexEndpoints.Filters(
                new QueryCollection(new Dictionary<string, StringValues> { [name] = values })));
    }

    [Fact]
    public void ReportIndex_filters_preserve_exact_case_whitespace_and_unicode_at_the_byte_boundary()
    {
        var tag = new string('é', 64);
        Assert.Equal((" Text ", tag), ReportIndexEndpoints.Filters(new QueryCollection(
            new Dictionary<string, StringValues> { ["kind"] = " Text ", ["tag"] = tag })));
        Assert.Equal((null, (string?)null), ReportIndexEndpoints.Filters(QueryCollection.Empty));
        foreach (var key in new[] { "Kind", "tags", "afterSeq", "blob_uri" })
            Assert.Throws<ArgumentException>(() => ReportIndexEndpoints.Filters(
                new QueryCollection(new Dictionary<string, StringValues> { [key] = "text" })));
    }

    [Fact]
    public void ReportIndex_ReportAdded_is_a_separate_camelcase_MessagePack_message_with_only_two_IDs()
    {
        var message = new ReportAdded(Guid.CreateVersion7().ToString("D"), Guid.CreateVersion7().ToString("D"));
        var bytes = MessagePackSerializer.Serialize(message, cancellationToken: TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(bytes,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["reportId", "runId"], json.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(message.RunId, json.RootElement.GetProperty("runId").GetString());
        Assert.Equal(message.ReportId, json.RootElement.GetProperty("reportId").GetString());
        Assert.DoesNotContain("seq", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
