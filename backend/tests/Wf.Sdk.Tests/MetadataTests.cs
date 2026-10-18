using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class MetadataTests
{
    [Fact]
    public void MissingOrMultipleTypesFail()
    {
        Assert.Contains("type", Assert.Throws<ArgumentException>(() =>
            Workflow.Define("missing", "1", _ => { })).Message);
        Assert.Throws<ArgumentException>(() => Workflow.Define("many", "1", w => w.Type("one").Type("two")));
        Assert.Throws<ArgumentException>(() => Workflow.Define("many", "1", w => w.Type("one").Type("one")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("team/type")]
    public void InvalidTypeNamesFail(string? type) =>
        Assert.ThrowsAny<ArgumentException>(() => Workflow.Define("invalid", "1", w => w.Type(type!)));

    [Fact]
    public void MetadataIsAnImmutableOrdinalSnapshot()
    {
        WorkflowBuilder? retained = null;
        var definition = Workflow.Define("metadata", "1", w =>
        {
            retained = w;
            w.Type("release").Metadata("area", "checkout").Metadata("Area", "payments").Metadata("empty", "");
        });
        retained!.Metadata("later", "ignored");
        Assert.Equal("release", definition.Type);
        Assert.Equal(3, definition.Metadata.Values.Count);
        Assert.Equal("checkout", definition.Metadata.Values["area"]);
        Assert.Equal("payments", definition.Metadata.Values["Area"]);
        Assert.Equal("", definition.Metadata.Values["empty"]);
    }

    [Fact]
    public void MetadataRejectsDuplicateKeysAndNullValues()
    {
        Assert.Contains("area", Assert.Throws<ArgumentException>(() => Workflow.Define("metadata", "1",
            w => w.Type("release").Metadata("area", "a").Metadata("area", "b"))).Message);
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("metadata", "1",
            w => w.Type("release").Metadata("area", null!)));
        Assert.Throws<ArgumentException>(() => Workflow.Define("metadata", "1",
            w => w.Type("release").Metadata(" ", "a")));
    }
}
