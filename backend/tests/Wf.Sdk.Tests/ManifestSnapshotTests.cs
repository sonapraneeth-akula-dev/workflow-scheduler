using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class ManifestSnapshotTests
{
    [Fact]
    public void Resource_limits_survive_source_generated_manifest_roundtrip_and_omission_stays_compatible()
    {
        var workflow = Workflow.Define("caps", "1", w => w.Type("test")
            .Node("work", n => n.Task("limited", _ => { }, p => p.Limits(cpuCores: 0.25, ramMb: 192))
                .Task("unlimited", _ => { })));
        var bytes = ManifestWriter.WriteUtf8(workflow);
        var manifest = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.Equal(new TaskResourceLimits(0.25, 192), manifest.Nodes[0].Tasks[0].Policy.Limits);
        Assert.Null(manifest.Nodes[0].Tasks[1].Policy.Limits);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest));
        using var document = JsonDocument.Parse(bytes);
        Assert.False(document.RootElement.GetProperty("nodes")[0].GetProperty("tasks")[1]
            .GetProperty("policy").TryGetProperty("limits", out _));
    }

    [Theory]
    [InlineData("""{"cpuCores":-1,"ramMb":0}""")]
    [InlineData("""{"cpuCores":0.001,"ramMb":0}""")]
    [InlineData("""{"cpuCores":0,"ramMb":18446744073709551615}""")]
    public void Invalid_caps_cannot_bypass_validation_through_manifest_JSON(string json) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Deserialize(json,
            WorkflowManifestJsonContext.Default.TaskResourceLimits));

    [Fact]
    public void TwoHundredNodeDescribePreservesEveryNodeTaskAndDependencyWithoutExecution()
    {
        var calls = 0;
        var workflow = Workflow.Define("benchmark-describe", "1", w =>
        {
            w.Type("benchmark").Metadata("area", "description").Variables(v => v.String("env", "dev"));
            for (var index = 0; index < 200; index++)
                w.Node($"node-{index}", n =>
                {
                    if (index > 0)
                        n.DependsOn($"node-{index - 1}");
                    n.Task("work", _ => { calls++; });
                });
        });
        var bytes = ManifestWriter.WriteUtf8(workflow);
        var manifest = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.Equal(200, manifest.Nodes.Length);
        for (var index = 0; index < manifest.Nodes.Length; index++)
        {
            var node = manifest.Nodes[index];
            Assert.Equal($"node-{index}", node.Key);
            Assert.Equal("work", Assert.Single(node.Tasks).Key);
            if (index == 0)
                Assert.Empty(node.Dependencies);
            else
                Assert.Equal($"node-{index - 1}", Assert.Single(node.Dependencies).Node);
        }
        Assert.Equal(bytes, ManifestWriter.WriteUtf8(workflow));
        Assert.Equal(0, calls);
    }

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
