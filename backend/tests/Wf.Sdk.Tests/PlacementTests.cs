using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;
using Wf.Validation;

namespace Wf.Sdk.Tests;

public sealed class PlacementTests
{
    [Fact]
    public void Innermost_selector_replaces_whole_selector_and_affinity_inherits_independently()
    {
        var workflow = Workflow.Define("placement", "1", w => w.Type("test")
            .RunsOn(Os.Linux, Machines.Pool("ci"), Machines.Label("dotnet"), Machines.Named("a", "b")).Affinity(Affinity.Run)
            .SystemInit(stage => stage.Task("init", _ => { }))
            .Node("build", n => n.RunsOn(Os.Windows).Affinity(Affinity.Node)
                .Task("compile", _ => { })
                .Task("test", _ => { }, t => t.RunsOn(Machines.Pool("smoke")).Affinity(Affinity.None)))
            .Node("check", n => n.DependsOn("build").Affinity(Affinity.SameMachineAs("build")).Task("check", _ => { })));
        var init = workflow.Nodes[0].Tasks[0].Placement!;
        Assert.Equal("linux", init.Selector!.Os!.Literal);
        Assert.Equal("ci", init.Selector.Pool!.Literal);
        Assert.Null(Assert.Single(init.Selector.Labels).Value);
        Assert.Equal(["a", "b"], init.Selector.Machines.Select(value => value.Literal));
        Assert.Equal(AffinityKind.Run, init.Affinity!.Kind);
        var build = workflow.Nodes[1].Tasks;
        Assert.Equal("windows", build[0].Placement!.Selector!.Os!.Literal);
        Assert.Null(build[0].Placement!.Selector!.Pool);
        Assert.Empty(build[0].Placement!.Selector!.Labels);
        Assert.Equal(AffinityKind.Node, build[0].Placement!.Affinity!.Kind);
        Assert.Equal("smoke", build[1].Placement!.Selector!.Pool!.Literal);
        Assert.Null(build[1].Placement!.Selector!.Os);
        Assert.Equal(AffinityKind.None, build[1].Placement!.Affinity!.Kind);
        Assert.Equal("build", workflow.Nodes[2].Tasks[0].Placement!.Affinity!.NodeKey);
    }

    [Fact]
    public void Defaults_and_post_stage_placement_are_inherited_and_can_be_cleared_explicitly()
    {
        var workflow = Workflow.Define("defaults", "1", w => w.Type("test")
            .Defaults(d => d.RunsOn(Os.Windows).Affinity(Affinity.Run))
            .RunsOn(Os.Linux)
            .PostProcess(stage => stage.RunsOn().Affinity(Affinity.None)
                .Task("cleanup", _ => { }, t => t.RunsOn(Machines.Pool("cleanup")))));
        var placement = workflow.Nodes.Single().Tasks.Single().Placement!;
        Assert.Equal("cleanup", placement.Selector!.Pool!.Literal);
        Assert.Null(placement.Selector.Os);
        Assert.Equal(AffinityKind.None, placement.Affinity!.Kind);
        Assert.Equal("linux", workflow.Placement!.Selector!.Os!.Literal);
        Assert.Equal(AffinityKind.Run, workflow.Placement.Affinity!.Kind);
    }

    [Fact]
    public void Variable_selectors_describe_symbolically_and_resolve_each_run_without_reexecuting_callback()
    {
        var configurationCalls = 0;
        var taskCalls = 0;
        var workflow = Workflow.Define("variables", "1", w => w.Type("test")
            .Variables(v => v.String("os", "linux").StringList("machines", ["a", "b"]).String("pool", "ci").String("sdk", "10"))
            .RunsOn(v =>
            {
                configurationCalls++;
                return Machines.All(Os.From(v["os"]), Machines.Named(v["machines"]), Machines.Pool(v["pool"]),
                    Machines.Label("dotnet", v["sdk"]));
            })
            .Node("work", n => n.Task("run", _ => { taskCalls++; })));
        var bytes = ManifestWriter.WriteUtf8(workflow);
        var manifest = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var template = manifest.Nodes[0].Tasks[0].Placement!.Selector!;
        Assert.Equal("machines", template.Machines.Single().Variable);
        Assert.Equal("os", template.Os!.Variable);
        var bound = VariableBinder.Bind(manifest.Variables, new Dictionary<string, JsonElement>());
        var resolved = PlacementResolver.Resolve(manifest.Nodes, bound)[0].Tasks[0].Placement!.Selector!;
        Assert.Equal(["a", "b"], resolved.Machines.Select(value => value.Literal));
        Assert.Equal("linux", resolved.Os!.Literal);
        Assert.Equal("ci", resolved.Pool!.Literal);
        Assert.Equal("10", resolved.Labels.Single().Value!.Literal);
        var another = PlacementResolver.Resolve(manifest.Nodes, bound.SetItem("machines", ImmutableArray.Create("c")));
        Assert.Equal("c", another[0].Tasks[0].Placement!.Selector!.Machines.Single().Literal);
        Assert.Equal("machines", template.Machines.Single().Variable);
        Assert.Equal(bytes, ManifestWriter.WriteUtf8(workflow));
        Assert.Equal(1, configurationCalls);
        Assert.Equal(0, taskCalls);
    }

    [Theory]
    [InlineData(VariableType.Secret)]
    [InlineData(VariableType.Int)]
    [InlineData(VariableType.Bool)]
    [InlineData(VariableType.StringList)]
    public void Scalar_selectors_reject_non_string_schema_without_exposing_values(VariableType type)
    {
        var node = new NodeDefinition("n", [], [new("t", placement: new(Machines.Pool(new(variable: "value"))))]);
        var exception = Assert.Throws<ArgumentException>(() => PlacementResolver.Validate([node], [new("value", type)]));
        Assert.Contains("n/t", exception.Message);
        Assert.Contains("value", exception.Message);
    }

    [Fact]
    public void Unknown_selector_variables_and_missing_runtime_values_fail_explicitly()
    {
        var exception = Assert.Throws<ArgumentException>(() => Workflow.Define("unknown", "1", w => w.Type("test")
            .Node("n", n => n.RunsOn(v => Machines.Named(v["missing"])).Task("t", _ => { }))));
        Assert.Contains("missing", exception.Message);
        var workflow = Workflow.Define("missing", "1", w => w.Type("test").Variables(v => v.String("machine"))
            .Node("n", n => n.RunsOn(v => Machines.Named(v["machine"])).Task("t", _ => { })));
        exception = Assert.Throws<ArgumentException>(() => PlacementResolver.Resolve(workflow.Nodes, ImmutableDictionary<string, object>.Empty));
        Assert.Contains("n/t", exception.Message);
        Assert.Contains("machine", exception.Message);
    }

    [Fact]
    public void Fully_overridden_outer_variable_is_not_evaluated_at_run_start()
    {
        var workflow = Workflow.Define("override", "1", w => w.Type("test").Variables(v => v.String("unused"))
            .RunsOn(v => Machines.Pool(v["unused"]))
            .Node("n", n => n.Task("t", _ => { }, t => t.RunsOn(Os.Linux))));
        var resolved = PlacementResolver.Resolve(workflow.Nodes, ImmutableDictionary<string, object>.Empty);
        Assert.Equal("linux", resolved[0].Tasks[0].Placement!.Selector!.Os!.Literal);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("n")]
    public void Same_machine_affinity_requires_another_known_node(string target) =>
        Assert.Throws<ArgumentException>(() => Workflow.Define("affinity", "1", w => w.Type("test")
            .Node("n", n => n.Affinity(Affinity.SameMachineAs(target)).Task("t", _ => { }))));

    [Fact]
    public void Affinity_cycles_and_pinning_to_a_downstream_node_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Workflow.Define("cycle", "1", w => w.Type("test")
            .Node("a", n => n.Affinity(Affinity.SameMachineAs("b")).Task("t", _ => { }))
            .Node("b", n => n.Affinity(Affinity.SameMachineAs("a")).Task("t", _ => { }))));
        Assert.Throws<ArgumentException>(() => Workflow.Define("downstream", "1", w => w.Type("test")
            .Node("a", n => n.Affinity(Affinity.SameMachineAs("b")).Task("t", _ => { }))
            .Node("b", n => n.DependsOn("a").Task("t", _ => { }))));
    }

    [Fact]
    public void Contradictory_combinations_and_invalid_contracts_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Machines.All(Os.Linux, Os.Windows));
        Assert.Throws<ArgumentException>(() => Machines.All(Machines.Pool("a"), Machines.Pool("b")));
        Assert.Throws<ArgumentException>(() => Machines.All(Machines.Named("a"), Machines.Named("b")));
        Assert.Throws<ArgumentException>(() => Machines.All(Machines.Label("a"), Machines.Label("a")));
        Assert.Throws<ArgumentException>(() => Machines.Named());
        Assert.Throws<ArgumentException>(() => new SelectorValue());
        Assert.Throws<ArgumentException>(() => new SelectorValue("literal", "variable"));
        Assert.Throws<ArgumentException>(() => new LabelSelector("key=value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlacementAffinity((AffinityKind)99));
        Assert.Throws<ArgumentException>(() => new PlacementAffinity(AffinityKind.Node, "other"));
        Assert.Throws<ArgumentException>(() => new PlacementAffinity(AffinityKind.SameMachineAs));
    }

    [Fact]
    public void Selector_constructor_copies_source_arrays_and_sorts_labels()
    {
        var labels = new[] { new LabelSelector("z"), new LabelSelector("a", "1") };
        var selector = new PlacementSelector(labels: labels.ToImmutableArray());
        labels[0] = new LabelSelector("changed");
        Assert.Equal(["a", "z"], selector.Labels.Select(label => label.Key));
    }
}
