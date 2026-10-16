using System.Collections.Immutable;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;

namespace Wf.Validation.Tests;

public sealed class SelectorValidatorTests
{
    private static readonly Guid Linux = Guid.Parse("0199ea00-0000-7000-8000-000000000001");
    private static readonly Guid Windows = Guid.Parse("0199ea00-0000-7000-8000-000000000002");
    private static readonly SelectorMachine[] Inventory =
    [
        new(Linux, "linux-agent", "linux", "ci", ImmutableDictionary<string, string>.Empty.Add("role", "perf").Add("empty", "")),
        new(Windows, "windows-agent", "windows", "desktop", ImmutableDictionary<string, string>.Empty.Add("role", "build"))
    ];

    [Fact]
    [RuleCoverage("WFV027", true)]
    public void Known_IDs_labels_OS_count_and_legacy_name_alternatives_are_valid()
    {
        foreach (var selector in new[]
        {
            Machines.Ids(Linux), Machines.All(Machines.Ids(Linux, Windows), Machines.Count(2)),
            Machines.All(Machines.Label("role", "perf"), Os.Linux, Machines.Count(1), Machines.Pool("ci")),
            Machines.All(Machines.Label("empty"), Os.Linux), Machines.Count(2),
            Machines.Named("offline-alternative", "linux-agent")
        })
            Assert.Empty(SelectorValidator.Validate(Manifest(selector), Inventory, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RuleCoverage("WFV027", false)]
    public void Unknown_ID_reports_exact_task_and_ID_without_partial_success()
    {
        var unknown = Guid.NewGuid();
        var error = Assert.Single(SelectorValidator.Validate(Manifest(Machines.Ids(Linux, unknown)), Inventory, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("WFV027", error.RuleId);
        Assert.Equal("$.nodes[0].tasks[0].placement.selector", error.Path);
        Assert.Contains("work/task", error.Hint);
        Assert.Contains(unknown.ToString("D"), error.Hint);
        Assert.DoesNotContain(Linux.ToString("D"), error.Hint);
    }

    [Theory]
    [InlineData("os")]
    [InlineData("pool")]
    [InlineData("name")]
    [InlineData("label")]
    [InlineData("label-value")]
    [InlineData("count")]
    [InlineData("id-os")]
    public void Unsatisfiable_constraints_fail_with_task_scope_but_never_echo_selector_values(string constraint)
    {
        var selector = constraint switch
        {
            "os" => Machines.All(Os.Linux, Machines.Ids(Windows)),
            "pool" => Machines.Pool("never-echo-this-value"),
            "name" => Machines.Named("never-echo-this-value"),
            "label" => Machines.Label("missing-label"),
            "label-value" => Machines.Label("role", "never-echo-this-value"),
            "count" => Machines.Count(3),
            "id-os" => Machines.All(Machines.Ids(Linux, Windows), Os.Linux),
            _ => throw new ArgumentOutOfRangeException(nameof(constraint))
        };
        var error = Assert.Single(SelectorValidator.Validate(Manifest(selector), Inventory, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("work/task", error.Hint);
        Assert.DoesNotContain("never-echo-this-value", error.ToString());
        if (constraint.StartsWith("label", StringComparison.Ordinal))
            Assert.Contains(constraint == "label" ? "missing-label" : "role", error.Hint);
    }

    [Fact]
    public void Registration_checks_literal_superset_without_binding_symbolic_values_or_defaults()
    {
        var manifest = Manifest(Machines.All(Os.Linux, Machines.Pool(new(variable: "pool")),
            Machines.Named(new SelectorValue(variable: "names")), Machines.Label("role", new(variable: "role")))) with
        {
            Variables = [new("pool", VariableType.String), new("names", VariableType.StringList), new("role", VariableType.String)]
        };
        Assert.Empty(SelectorValidator.Validate(manifest, Inventory, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(SelectorValidator.Validate(manifest, Inventory, ImmutableDictionary<string, object>.Empty
            .Add("pool", "ci").Add("names", ImmutableArray.Create("linux-agent")).Add("role", "perf"), TestContext.Current.CancellationToken));
        var error = Assert.Single(SelectorValidator.Validate(manifest, Inventory, ImmutableDictionary<string, object>.Empty
            .Add("pool", "ci").Add("names", ImmutableArray.Create("linux-agent")).Add("role", "never-echo-this-value"), TestContext.Current.CancellationToken));
        Assert.Contains("role", error.Hint);
        Assert.DoesNotContain("never-echo-this-value", error.ToString());
        Assert.Single(SelectorValidator.Validate(manifest, Inventory, ImmutableDictionary<string, object>.Empty, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(VariableType.Int)]
    [InlineData(VariableType.Secret)]
    [InlineData(VariableType.StringList)]
    public void Invalid_scalar_reference_schema_fails_even_during_partial_registration_validation(VariableType type)
    {
        var manifest = Manifest(Machines.Pool(new(variable: "value"))) with { Variables = [new("value", type)] };
        var error = Assert.Single(SelectorValidator.Validate(manifest, Inventory, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("value", error.Hint);
        Assert.Equal("WFV027", error.RuleId);
    }

    [Fact]
    public void Only_effective_innermost_selector_is_checked_and_original_manifest_stays_unchanged()
    {
        var outer = new PlacementPolicy(Machines.Ids(Guid.NewGuid()));
        var manifest = Manifest(null) with
        {
            Placement = outer,
            Nodes = [new("work", [], [new("task", placement: new(Machines.Ids(Linux)))],
                placement: new(Machines.Pool(new(variable: "fully-overridden"))))]
        };
        Assert.Empty(SelectorValidator.Validate(manifest, Inventory, ImmutableDictionary<string, object>.Empty, TestContext.Current.CancellationToken));
        Assert.Same(outer, manifest.Placement);
        Assert.Equal("fully-overridden", manifest.Nodes[0].Placement!.Selector!.Pool!.Variable);
        Assert.Equal([Linux], manifest.Nodes[0].Tasks[0].Placement!.Selector!.MachineIds);
    }

    [Fact]
    public void Empty_effective_selector_clears_outer_constraints_and_needs_no_inventory()
    {
        var manifest = Manifest(Machines.All()) with { Placement = new(Machines.Ids(Guid.NewGuid())) };
        Assert.False(SelectorValidator.RequiresInventory(manifest));
        Assert.Empty(SelectorValidator.Validate(manifest, [], cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(SelectorValidator.RequiresInventory(Manifest(Machines.Count(1))));
        Assert.Single(SelectorValidator.Validate(Manifest(Os.Linux), [], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<OperationCanceledException>(() => SelectorValidator.Validate(manifest, [], cancellationToken: new(true)));
    }

    [Fact]
    public void Corrupt_inventory_fails_explicitly_instead_of_accepting_or_deduplicating_it()
    {
        Assert.Throws<ArgumentException>(() => SelectorValidator.Validate(Manifest(null), [Inventory[0], Inventory[0]], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => SelectorValidator.Validate(Manifest(null), [Inventory[0] with { Id = Guid.Empty }], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => SelectorValidator.Validate(Manifest(null), [Inventory[0] with { Os = "unknown" }], cancellationToken: TestContext.Current.CancellationToken));
    }

    private static WorkflowManifest Manifest(PlacementSelector? selector) => new("1.0.0", "demo/test/selector", "1.0.0",
        new("test", ImmutableSortedDictionary<string, string>.Empty), [], [],
        [new("work", [], [new("task", placement: selector is null ? null : new(selector))])], TimeSpan.FromHours(1));
}
