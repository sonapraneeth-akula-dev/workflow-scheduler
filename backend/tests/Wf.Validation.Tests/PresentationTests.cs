using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Registry;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class PresentationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [RuleCoverage("WFV024", true)]
    public void Ordered_presentation_round_trips_through_the_shared_team_contract()
    {
        var registry = Registry() with
        {
            Presentation = new(["cleanup", "release"], ["environment"], ["component", "environment"],
                Map("environment", "dev"), Map("environment", "Environment"))
        };
        Assert.Empty(Check(registry));
        var serialized = JsonSerializer.SerializeToUtf8Bytes(registry, TeamRegistryJsonContext.Default.TeamRegistry);
        Assert.Empty(TeamRegistryRules.Validate(serialized, cancellationToken: Token));
        var restored = JsonSerializer.Deserialize(serialized, TeamRegistryJsonContext.Default.TeamRegistry)!;
        Assert.Equal(["cleanup", "release"], restored.Presentation!.TypeOrder);
        Assert.Equal(["component", "environment"], restored.Presentation.ListColumns);
        Assert.Equal("dev", restored.Presentation.DefaultFilters["environment"]);
        Assert.Equal("Environment", restored.Presentation.Labels["environment"]);
        Assert.Equal("release", restored.Types[0].Name);
        Assert.Equal(serialized, JsonSerializer.SerializeToUtf8Bytes(restored, TeamRegistryJsonContext.Default.TeamRegistry));
    }

    [Fact]
    public void Absent_null_or_empty_presentation_preserves_existing_registry_behavior()
    {
        Assert.Empty(Check(Registry()));
        Assert.Empty(Check(Registry() with { Presentation = new() }));
        var registry = Read("""{"team":"demo","types":[],"presentation":{}}""");
        Assert.Empty(Check(registry));
        Assert.Empty(registry.Presentation!.TypeOrder);
        Assert.Empty(registry.Presentation.GroupingKeys);
        Assert.Empty(registry.Presentation.ListColumns);
        Assert.Empty(registry.Presentation.DefaultFilters);
        Assert.Empty(registry.Presentation.Labels);
        Assert.Empty(Check(Read("""{"team":"demo","types":[],"presentation":null}""")));
    }

    [Theory]
    [InlineData("typeOrder", "unknown")]
    [InlineData("typeOrder", "Release")]
    [InlineData("typeOrder", " ")]
    [InlineData("groupingKeys", "unknown")]
    [InlineData("groupingKeys", "Environment")]
    [InlineData("groupingKeys", " ")]
    [InlineData("listColumns", "unknown")]
    [InlineData("listColumns", "Environment")]
    [InlineData("listColumns", " ")]
    [RuleCoverage("WFV024", false)]
    public void Unknown_empty_or_wrong_case_references_name_the_exact_index(string property, string value)
    {
        var presentation = WithList(property, [value]);
        AssertError(Assert.Single(Check(Registry() with { Presentation = presentation })), $"$.presentation.{property}[0]");
    }

    [Theory]
    [InlineData("typeOrder", "release")]
    [InlineData("groupingKeys", "environment")]
    [InlineData("listColumns", "environment")]
    public void Duplicate_references_are_not_silently_deduplicated(string property, string value)
    {
        var presentation = WithList(property, [value, value]);
        AssertError(Assert.Single(Check(Registry() with { Presentation = presentation })), $"$.presentation.{property}[1]");
    }

    [Theory]
    [InlineData("environment", "unknown")]
    [InlineData("environment", "DEV")]
    [InlineData("environment", " ")]
    [InlineData("unknown", "dev")]
    public void Default_filter_keys_and_values_must_match_a_declared_metadata_schema(string key, string value) =>
        AssertError(Assert.Single(Check(Registry() with { Presentation = new(DefaultFilters: Map(key, value)) })),
            $"$.presentation.defaultFilters[\"{key}\"]");

    [Theory]
    [InlineData("environment", "dev")]
    [InlineData("environment", "prod")]
    [InlineData("region", "eu")]
    [InlineData("component", "any component")]
    public void Team_defaults_can_filter_a_subset_of_types_or_unconstrained_declared_values(string key, string value) =>
        Assert.Empty(Check(Registry() with { Presentation = new(DefaultFilters: Map(key, value)) }));

    [Theory]
    [InlineData("unknown", "Label")]
    [InlineData("Environment", "Label")]
    [InlineData("environment", "")]
    [InlineData("environment", " ")]
    public void Labels_are_nonempty_and_reference_declared_metadata_keys(string key, string label) =>
        AssertError(Assert.Single(Check(Registry() with { Presentation = new(Labels: Map(key, label)) })),
            $"$.presentation.labels[\"{key}\"]");

    [Fact]
    public void Free_form_metadata_does_not_authorize_unknown_presentation_keys()
    {
        var registry = Registry();
        registry = registry with { Types = [registry.Types[0] with
        {
            MetadataSchema = registry.Types[0].MetadataSchema with { AllowFreeForm = true }
        }], Presentation = new(GroupingKeys: ["not-declared"]) };
        AssertError(Assert.Single(Check(registry)), "$.presentation.groupingKeys[0]");
    }

    [Fact]
    public void Errors_are_stably_ordered_and_never_echo_filter_or_label_values()
    {
        var registry = Registry() with { Presentation = new(GroupingKeys: ["unknown"], ListColumns: ["other"],
            DefaultFilters: Map("environment", "sensitive-filter-value"), Labels: Map("bad", "sensitive-label-value")) };
        var errors = Check(registry);
        Assert.Equal(4, errors.Length);
        Assert.Equal(errors.OrderBy(error => error.Path, StringComparer.Ordinal), errors);
        Assert.All(errors, error =>
        {
            Assert.Equal("WFV024", error.RuleId);
            Assert.DoesNotContain("sensitive", error.Message + error.Hint + error.Path);
        });
    }

    [Theory]
    [InlineData("""{"unknown":true}""")]
    [InlineData("""{"groupingKeys":null}""")]
    [InlineData("""{"listColumns":[42]}""")]
    [InlineData("""{"defaultFilters":null}""")]
    [InlineData("""{"labels":{"environment":42}}""")]
    [InlineData("""{"typeOrder":[],"typeOrder":[]}""")]
    [InlineData("""{"defaultFilters":{"environment":"dev","environment":"ppe"}}""")]
    public void Malformed_presentation_returns_sanitized_registry_shape_error(string presentation)
    {
        var json = $$"""{"team":"demo","types":[],"presentation":{{presentation}}}""";
        var error = Assert.Single(TeamRegistryRules.Validate(Encoding.UTF8.GetBytes(json), cancellationToken: Token));
        Assert.Equal(TeamRegistryRules.RegistryShape.CreateError("team.json", "$"), error);
    }

    [Fact]
    public void Invalid_typed_collection_shapes_and_null_values_are_errors_not_exceptions()
    {
        TeamPresentation[] invalid =
        [
            new() { TypeOrder = default },
            new() { GroupingKeys = default },
            new() { ListColumns = default },
            new() { DefaultFilters = null! },
            new() { Labels = null! },
            new(Labels: Map("environment", null!)),
            new(DefaultFilters: Map("environment", null!))
        ];
        foreach (var presentation in invalid)
            Assert.Equal("WFV024", Assert.Single(Check(Registry() with { Presentation = presentation })).RuleId);
        var broken = Registry() with { Types = default, Presentation = new() };
        Assert.Equal("WFV024", Assert.Single(PresentationValidator.Validate(broken, cancellationToken: Token)).RuleId);
    }

    [Fact]
    public void Cancellation_and_argument_errors_are_not_hidden_by_an_empty_presentation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PresentationValidator.Validate(Registry(), cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => PresentationValidator.Validate(null!, cancellationToken: Token));
        Assert.Throws<ArgumentException>(() => PresentationValidator.Validate(Registry(), file: "", cancellationToken: Token));
        Assert.Contains(PresentationValidator.InvalidPresentation, RuleCatalog.All);
    }

    private static TeamPresentation WithList(string property, ImmutableArray<string> values) => property switch
    {
        "typeOrder" => new(TypeOrder: values),
        "groupingKeys" => new(GroupingKeys: values),
        "listColumns" => new(ListColumns: values),
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };

    private static ImmutableDictionary<string, string> Map(string key, string value) =>
        ImmutableDictionary<string, string>.Empty.Add(key, value);

    private static TeamRegistry Registry() => new("demo",
    [
        new("demo", "release", "Release", new([
            new("environment", AllowedValues: ["dev", "ppe"]), new("component")
        ])),
        new("demo", "cleanup", "Cleanup", new([
            new("environment", AllowedValues: ["prod"]), new("region", AllowedValues: ["us", "eu"])
        ]))
    ]);

    private static ImmutableArray<ValidationError> Check(TeamRegistry registry) =>
        TeamRegistryRules.Validate(registry, cancellationToken: Token);

    private static TeamRegistry Read(string json) => JsonSerializer.Deserialize(json, TeamRegistryJsonContext.Default.TeamRegistry)!;

    private static void AssertError(ValidationError error, string path) =>
        Assert.Equal(PresentationValidator.InvalidPresentation.CreateError("team.json", path), error);
}
