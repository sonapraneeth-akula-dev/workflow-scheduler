using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class ManifestSnapshotTests
{
    [Fact]
    public void ManifestMatchesGoldenFile()
    {
        var workflow = Workflow.Define("payments/release/deploy", "1.0.0",
            w => w.Type("release").Metadata("area", "checkout").Node("build", _ => { }));
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "workflow.manifest.json"))
            .TrimEnd('\r', '\n');
        Assert.Equal(expected.Replace("\r\n", "\n"), ManifestWriter.Write(workflow).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task DescribeCarriesAllResolvedContractsWithoutInvokingDelegates()
    {
        var calls = 0;
        var workflow = Workflow.Define("payments/release/deploy", "2.1.0", w => w.Type("release")
            .Metadata("area", "checkout").Variables(v => v.String("env", required: true, allowed: ["dev", "prod"]))
            .Defaults(d => d.Retry(2)).SystemInit(_ => { calls++; })
            .Node("build", n => n.Task("compile", _ => { calls++; }, failure: Failure.Soft))
            .Node("cleanup", n => n.DependsOn("build", DependencyCondition.Always).Input("build", "version")
                .Parallel(2).Timeout(TimeSpan.FromMinutes(5)).Task("work", _ => { calls++; }, t => t.NoAutoRetry()),
                failure: Failure.Soft)
            .PostProcess(_ => { calls++; }));
        using var stream = new MemoryStream();
        await ManifestWriter.WriteAsync(workflow, stream, TestContext.Current.CancellationToken);
        Assert.Equal(ManifestWriter.WriteUtf8(workflow), stream.ToArray());
        var manifest = JsonSerializer.Deserialize(stream.ToArray(), WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.Equal("release", manifest.Metadata.Type);
        Assert.Equal("checkout", manifest.Metadata.Values["area"]);
        Assert.True(Assert.Single(manifest.Variables).Required);
        Assert.Equal(["$init", "$post"], manifest.Stages);
        Assert.Equal(Failure.Soft, manifest.Nodes[1].Tasks[0].Failure);
        Assert.Equal(Failure.Soft, manifest.Nodes[2].Failure);
        Assert.Equal(DependencyCondition.Always, manifest.Nodes[2].Dependencies[0].When);
        Assert.Equal(new InputBinding("build", "version"), Assert.Single(manifest.Nodes[2].Inputs));
        Assert.Equal(2, manifest.Nodes[2].MaxParallel);
        Assert.Equal(2, manifest.Nodes[2].Tasks[0].Policy.Retry.Retries);
        Assert.False(manifest.Nodes[2].Tasks[0].Policy.AutoRetry);
        Assert.Equal(TimeSpan.FromMinutes(5), manifest.Nodes[2].Tasks[0].Policy.AttemptTimeout);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("execute", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataOrderingDoesNotChangeManifestBytes()
    {
        var first = Workflow.Define("metadata", "1", w => w.Type("test").Metadata("z", "1").Metadata("a", "2"));
        var second = Workflow.Define("metadata", "1", w => w.Type("test").Metadata("a", "2").Metadata("z", "1"));
        Assert.Equal(ManifestWriter.Write(first), ManifestWriter.Write(second));
        Assert.Equal(["a", "z"], first.Metadata.Values.Keys);
    }
}
