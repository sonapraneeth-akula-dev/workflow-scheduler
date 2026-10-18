using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;
using Wf.Validation;

namespace Wf.Sdk.Tests;

public sealed class SelectorTests
{
    [Fact]
    public void Machine_ids_count_and_os_survive_manifest_roundtrip_and_runtime_binding()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var workflow = Workflow.Define("ids", "1.0.0", w => w.Type("test")
            .RunsOn(Machines.Ids(ids), Machines.Count(2), Os.Linux)
            .Node("work", n => n.Task("run", _ => { })));
        ids[0] = Guid.Empty;
        var bytes = ManifestWriter.WriteUtf8(workflow);
        var manifest = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var selector = manifest.Nodes[0].Tasks[0].Placement!.Selector!;
        Assert.Equal(workflow.Placement!.Selector!.MachineIds, selector.MachineIds);
        Assert.DoesNotContain(Guid.Empty, selector.MachineIds);
        Assert.Equal(2, selector.Count);
        Assert.Equal("linux", selector.Os!.Literal);
        Assert.Contains("\"machineIds\"", Encoding.UTF8.GetString(bytes));
        Assert.Contains("\"count\": 2", Encoding.UTF8.GetString(bytes));
        var resolved = PlacementResolver.Resolve(manifest.Nodes, ImmutableDictionary<string, object>.Empty);
        Assert.Equal(selector.MachineIds, resolved[0].Tasks[0].Placement!.Selector!.MachineIds);
        Assert.Equal(2, resolved[0].Tasks[0].Placement!.Selector!.Count);
        Assert.Equal(bytes, ManifestWriter.WriteUtf8(workflow));
    }

    [Fact]
    public void Label_count_and_os_compose_with_symbolic_pool_and_preserve_whole_selector_inheritance()
    {
        var id = Guid.NewGuid();
        var workflow = Workflow.Define("scopes", "1.0.0", w => w.Type("test")
            .Variables(v => v.String("pool", "ci"))
            .RunsOn(Machines.Ids(id), Machines.Count(1), Os.Windows).Affinity(Affinity.Run)
            .SystemInit(s => s.Task("init", _ => { }))
            .Node("work", n => n.RunsOn(v => Machines.All(Machines.Label("role", "perf"), Machines.Count(3),
                    Os.Linux, Machines.Pool(v["pool"])))
                .Task("inherited", _ => { })
                .Task("override", _ => { }, t => t.RunsOn(Machines.Ids(id)))
                .Task("clear", _ => { }, t => t.RunsOn())));
        var manifest = JsonSerializer.Deserialize(ManifestWriter.WriteUtf8(workflow),
            WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var nodes = PlacementResolver.Resolve(manifest.Nodes, VariableBinder.Bind(manifest.Variables, new Dictionary<string, JsonElement>()));
        var init = nodes[0].Tasks[0].Placement!;
        Assert.Equal([id], init.Selector!.MachineIds);
        Assert.Equal(1, init.Selector.Count);
        var inherited = nodes[1].Tasks[0].Placement!;
        Assert.Empty(inherited.Selector!.MachineIds);
        Assert.Equal(3, inherited.Selector.Count);
        Assert.Equal("linux", inherited.Selector.Os!.Literal);
        Assert.Equal("ci", inherited.Selector.Pool!.Literal);
        Assert.Equal("perf", Assert.Single(inherited.Selector.Labels).Value!.Literal);
        Assert.All(nodes.SelectMany(n => n.Tasks), t => Assert.Equal(AffinityKind.Run, t.Placement!.Affinity!.Kind));
        var overridden = nodes[1].Tasks[1].Placement!.Selector!;
        Assert.Equal([id], overridden.MachineIds);
        Assert.Null(overridden.Count);
        Assert.Null(overridden.Os);
        Assert.Empty(overridden.Labels);
        var cleared = nodes[1].Tasks[2].Placement!.Selector!;
        Assert.Empty(cleared.MachineIds);
        Assert.Empty(cleared.Labels);
        Assert.Null(cleared.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Nonpositive_counts_fail_at_authoring_and_json_boundary(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Machines.Count(count));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonSerializer.Deserialize($$"""{"count":{{count}}}""", WorkflowManifestJsonContext.Default.PlacementSelector));
    }

    [Fact]
    public void Invalid_or_contradictory_ids_counts_and_alternatives_fail_explicitly()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentNullException>(() => Machines.Ids(null!));
        Assert.Throws<ArgumentException>(() => Machines.Ids());
        Assert.Throws<ArgumentException>(() => Machines.Ids(Guid.Empty));
        Assert.Throws<ArgumentException>(() => Machines.All(Machines.Ids(id), Machines.Named("same-machine")));
        Assert.Throws<ArgumentException>(() => Machines.All(Machines.Ids(id), Machines.Ids(Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => Machines.All(Machines.Count(1), Machines.Count(2)));
        Assert.Throws<ArgumentException>(() => Machines.All(Machines.Ids(id, id), Machines.Count(2)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"machineIds":["invalid"]}""",
            WorkflowManifestJsonContext.Default.PlacementSelector));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize("""{"machineIds":["00000000-0000-0000-0000-000000000000"]}""",
            WorkflowManifestJsonContext.Default.PlacementSelector));
        var combined = Machines.All(Machines.Ids(id, id), Machines.Count(1), Machines.Count(1));
        Assert.Equal([id], combined.MachineIds);
        Assert.Equal(1, combined.Count);
    }

    [Fact]
    public void Legacy_json_without_new_fields_retains_names_and_has_unspecified_count()
    {
        var selector = JsonSerializer.Deserialize("""{"os":{"literal":"linux"},"machines":[{"literal":"worker"}]}""",
            WorkflowManifestJsonContext.Default.PlacementSelector)!;
        Assert.Empty(selector.MachineIds);
        Assert.Null(selector.Count);
        Assert.Equal("worker", Assert.Single(selector.Machines).Literal);
        Assert.Equal("linux", selector.Os!.Literal);
        Assert.Equal(int.MaxValue, Machines.Count(int.MaxValue).Count);
    }
}
