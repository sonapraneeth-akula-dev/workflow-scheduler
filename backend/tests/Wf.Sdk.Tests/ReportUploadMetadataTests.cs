using System.Text.Json;
using Wf.Contracts.Reports;

namespace Wf.Sdk.Tests;

public sealed class ReportUploadMetadataTests
{
    [Theory]
    [InlineData("kind")]
    [InlineData("title")]
    [InlineData("tag")]
    [InlineData("tag-count")]
    [InlineData("hash-uppercase")]
    [InlineData("hash-invalid")]
    [InlineData("size")]
    public void ReportPublication_rejects_unsafe_or_oversized_metadata(string scenario)
    {
        var metadata = Valid();
        metadata = scenario switch
        {
            "kind" => metadata with { Kind = new string('x', 129) },
            "title" => metadata with { Title = new string('x', 513) },
            "tag" => metadata with { Tags = [new string('x', 129)] },
            "tag-count" => metadata with { Tags = Enumerable.Repeat("tag", 33).ToArray() },
            "hash-uppercase" => metadata with { Sha256 = new string('A', 64) },
            "hash-invalid" => metadata with { Sha256 = new string('g', 64) },
            _ => metadata with { SizeBytes = -1 }
        };
        Assert.ThrowsAny<ArgumentException>(metadata.Validate);
    }

    [Fact]
    public void ReportPublication_uses_UTF8_byte_bounds_and_preserves_unknown_kind_and_exact_metadata()
    {
        var valid = Valid() with
        {
            Kind = "Unknown custom kind", Title = " exact " + new string('\u00e9', 250),
            Tags = Enumerable.Repeat(new string('\u00e9', 64), 32).ToArray(), SizeBytes = 0
        };
        valid.Validate();
        var invalidTitle = valid with { Title = new string('\u00e9', 257) };
        Assert.Throws<ArgumentException>(invalidTitle.Validate);
        var invalidTag = valid with { Tags = [new string('\u00e9', 65)] };
        Assert.Throws<ArgumentException>(invalidTag.Validate);
        var json = JsonSerializer.Serialize(valid, ReportJsonContext.Default.ReportUploadMetadata);
        var restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.ReportUploadMetadata)!;
        Assert.Equal(valid.Kind, restored.Kind);
        Assert.Equal(valid.Title, restored.Title);
        Assert.Equal(valid.Tags, restored.Tags);
        Assert.Equal(0, restored.SizeBytes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"runId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"reportId":"00000000-0000-0000-0000-000000000000"}""")]
    public void ReportPublication_receipt_requires_both_identity_fields(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, ReportJsonContext.Default.ReportReceipt));

    [Fact]
    public async Task ReportPublication_local_publisher_checks_same_description_bounds_before_sink()
    {
        var calls = 0;
        var publisher = new ReportPublisher((_, _) => { calls++; return Task.CompletedTask; });
        var workflow = Workflow.Define("metadata", "1", w => w.Type("test").Node("work", n => n
            .Task("report", ctx => ctx.Reports.PublishAsync("text", new string('x', 513),
                ReadOnlyMemory<byte>.Empty, ctx.CancellationToken))));
        await Assert.ThrowsAsync<ArgumentException>(() => new TaskRegistry(workflow).ExecuteAsync("work/report",
            new(TestContext.Current.CancellationToken, reports: publisher)));
        Assert.Equal(0, calls);
    }

    private static ReportUploadMetadata Valid() => new("text", "Summary", ["review"], new string('a', 64), 1);
}
