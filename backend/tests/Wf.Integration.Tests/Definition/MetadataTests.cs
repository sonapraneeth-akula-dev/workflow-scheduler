using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Integration.Tests.Definition;

public sealed class MetadataTests
{
    [Fact]
    public void WorkflowMetadataRoundTripsWithoutLosingTypeOrFreeFormKeys()
    {
        var workflow = Workflow.Define("payments/release/deploy", "1", w => w.Type("release")
            .Metadata("area", "checkout").Metadata("build-number", "123"));
        var json = JsonSerializer.Serialize(workflow.Metadata);
        var metadata = JsonSerializer.Deserialize<WorkflowMetadata>(json)!;
        Assert.Equal("release", metadata.Type);
        Assert.Equal("checkout", metadata.Values["area"]);
        Assert.Equal("123", metadata.Values["build-number"]);
    }
}
