using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Manifest;
using Wf.Sdk;
using Wf.Sdk.Local;
using Wf.Validation;

namespace Wf.Integration.Tests.Definition;

public sealed class PlacementTests
{
    [Fact]
    public async Task Manifest_variable_placement_and_local_run_share_the_same_bound_values()
    {
        var calls = 0;
        var workflow = Workflow.Define("placement", "1", w => w.Type("test")
            .Variables(v => v.StringList("machines", ["default"]).String("pool", "ci"))
            .RunsOn(v => Machines.All(Machines.Named(v["machines"]), Machines.Pool(v["pool"])))
            .Affinity(Affinity.Node)
            .Node("work", n => n.Task("run", context =>
            {
                Assert.Equal(["worker-a", "worker-b"], context.Var<ImmutableArray<string>>("machines"));
                calls++;
            })));
        var manifest = JsonSerializer.Deserialize(ManifestWriter.WriteUtf8(workflow), WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var values = new Dictionary<string, JsonElement>
        {
            ["machines"] = JsonSerializer.SerializeToElement(new[] { "worker-a", "worker-b" })
        };
        var bound = VariableBinder.Bind(manifest.Variables, values);
        var resolved = PlacementResolver.Resolve(manifest.Nodes, bound);
        Assert.Equal(["worker-a", "worker-b"], resolved[0].Tasks[0].Placement!.Selector!.Machines.Select(value => value.Literal));
        var result = await new LocalEngine(workflow, values, output: TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(1, calls);
        Assert.Equal("machines", manifest.Nodes[0].Tasks[0].Placement!.Selector!.Machines.Single().Variable);
    }

    [Theory]
    [InlineData("""{"machines":[]}""")]
    [InlineData("""{"machines":[""]}""")]
    [InlineData("""{"machines":["valid"],"os":"unsupported"}""")]
    public void Invalid_runtime_selector_fails_before_original_task_execution(string json)
    {
        var calls = 0;
        var workflow = Workflow.Define("invalid", "1", w => w.Type("test")
            .Variables(v => v.StringList("machines").String("os", "linux"))
            .RunsOn(v => Machines.All(Machines.Named(v["machines"]), Os.From(v["os"])))
            .Node("work", n => n.Task("run", _ => { calls++; })));
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Throws<ArgumentException>(() => new LocalEngine(workflow, values, output: TextWriter.Null));
        Assert.Equal(0, calls);
    }
}
