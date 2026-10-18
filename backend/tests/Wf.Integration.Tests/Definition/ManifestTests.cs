using System.Text.Json;
using Wf.Contracts.Manifest;
using Wf.Engine.Core;
using Wf.Sdk;

namespace Wf.Integration.Tests.Definition;

public sealed class ManifestTests
{
    [Fact]
    public void DescribedDefinitionCreatesCoreRunWithIdenticalTypeMetadataAndTaskIdentity()
    {
        var workflow = Workflow.Define("payments/release/deploy", "1", w => w.Type("release")
            .Metadata("area", "checkout").SystemInit(_ => { })
            .Node("build", n => n.Task("compile", _ => { })));
        var manifest = JsonSerializer.Deserialize(ManifestWriter.WriteUtf8(workflow),
            WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var run = new RunState(Guid.CreateVersion7(), manifest.Nodes, manifest.Metadata, Assert.Fail);
        Assert.Equal("release", run.Snapshot.Metadata.Type);
        Assert.Equal("checkout", run.Snapshot.Metadata.Values["area"]);
        Assert.Equal(workflow.Tasks.Keys.Order(), run.Snapshot.Tasks.Keys.Order());
    }
}
