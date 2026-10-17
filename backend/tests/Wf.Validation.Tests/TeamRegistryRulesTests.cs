using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.Sdk;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class TeamRegistryRulesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [RuleCoverage("WFV015", true)]
    public void ValidRegistryAndTypedContractUseTheSameRules()
    {
        var registry = Registry();
        var utf8 = Write(registry);
        var roundTrip = JsonSerializer.Deserialize(utf8, TeamRegistryJsonContext.Default.TeamRegistry)!;
        Assert.Equal("demo", roundTrip.Team);
        Assert.Equal("release", Assert.Single(roundTrip.Types).Name);
        Assert.Empty(TeamRegistryRules.Validate(utf8, cancellationToken: Token));
        Assert.Empty(TeamRegistryRules.Validate(roundTrip, cancellationToken: Token));
        Assert.Empty(TeamRegistryRules.Validate(Write(new("demo", [])), cancellationToken: Token));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"team":"demo","types":null}""")]
    [InlineData("""{"team":"demo","team":"other","types":[]}""")]
    [InlineData("""{"team":"demo","types":[],"unknown":"do-not-echo"}""")]
    [InlineData("""{"team":"demo","types":[{"name":"release","description":"Release","metadataSchema":{"keys":[]}}]}""")]
    [InlineData("""{"team":"demo","types":[{"team":"demo","description":"Release","metadataSchema":{"keys":[]}}]}""")]
    [InlineData("""{"team":"demo","types":[{"team":"demo","name":"release","description":"Release"}]}""")]
    [InlineData("""{"team":"demo","types":[{"team":"demo","name":"release","description":"Release","metadataSchema":{"keys":[],"allowFreeForm":"true"}}]}""")]
    [InlineData("""{"team":"demo","types":[{"team":"demo","name":"release","description":"Release","metadataSchema":{"keys":[{"key":"env","allowedValues":[42]}]}}]}""")]
    [RuleCoverage("WFV015", false)]
    public void MalformedContractsReturnSanitizedShapeErrors(string json) =>
        AssertError(Assert.Single(Check(json)), TeamRegistryRules.RegistryShape, "$");

    [Fact]
    public void ContractPropertiesCannotBeDuplicatedAtAnyDepth()
    {
        const string json = """
            {"team":"demo","types":[{"team":"demo","name":"release","description":"Release",
             "metadataSchema":{"keys":[{"key":"environment","required":true,"required":false}]}}]}
            """;
        AssertError(Assert.Single(Check(json)), TeamRegistryRules.RegistryShape, "$");
    }

    [Theory]
    [InlineData("team", "$.types[0].team")]
    [InlineData("name", "$.types[0].name")]
    [InlineData("description", "$.types[0].description")]
    public void RequiredIdentifiersAndDescriptionCannotBeBlank(string property, string path)
    {
        var type = Registry().Types[0];
        type = property switch
        {
            "team" => type with { Team = " " },
            "name" => type with { Name = " " },
            _ => type with { Description = " " }
        };
        AssertError(Assert.Single(TeamRegistryRules.Validate(Registry() with { Types = [type] },
            cancellationToken: Token)), TeamRegistryRules.RegistryShape, path);
    }

    [Fact]
    public void OwningTeamMustMatchTheRegistry()
    {
        var registry = Registry();
        AssertError(Assert.Single(TeamRegistryRules.Validate(Write(registry with
        {
            Types = [registry.Types[0] with { Team = "another-team" }]
        }), cancellationToken: Token)), TeamRegistryRules.RegistryShape, "$.types[0].team");
        AssertError(Assert.Single(Check("""{"team":" ","types":[]}""")),
            TeamRegistryRules.RegistryShape, "$.team");
    }

    [Theory]
    [InlineData("release", "release")]
    [InlineData("Release Tests", "release-tests")]
    [InlineData("Release_Tests", "RELEASE.TEST!")]
    [InlineData("release-test", "release-tests")]
    [InlineData("release", "releaze")]
    [InlineData("release", "releazeq")]
    [InlineData("release", "relea")]
    [InlineData("release", "releasesx")]
    [InlineData("Load-Test", "load_tests")]
    [InlineData("Load Test", "LOAD.TESTS!")]
    [InlineData("\"Load-Test\"", "load_tests")]
    [InlineData("<Load-Test>", "load_tests")]
    [RuleCoverage("WFV016", false)]
    public void NearDuplicateNamesAreRejectedWithEscapedConflictNameAndLocation(string first, string second)
    {
        var error = Assert.Single(TeamRegistryRules.Validate(Write(Registry(first, second)), cancellationToken: Token));
        AssertError(error, TeamRegistryRules.DuplicateType, "$.types[1].name");
        Assert.Contains("$.types[0].name", error.Hint);
        Assert.Contains("Conflicting type: " + JsonSerializer.Serialize(first), error.Hint);
        Assert.DoesNotContain('<', error.Hint);
        Assert.DoesNotContain('\n', error.Hint);
    }

    [Theory]
    [InlineData("release", "cleanup")]
    [InlineData("release", "releaseabc")]
    [InlineData("release", "qwertyu")]
    [RuleCoverage("WFV016", true)]
    public void NearDuplicateThresholdAllowsNamesMoreThanTwoEditsApart(string first, string second) =>
        Assert.Empty(TeamRegistryRules.Validate(Write(Registry(first, second)), cancellationToken: Token));

    [Fact]
    public void NearDuplicateNamesInAnotherTeamAreAllowed()
    {
        var registry = Registry();
        Assert.Empty(TeamRegistryRules.Validate(Write(registry), cancellationToken: Token));
        Assert.Empty(TeamRegistryRules.Validate(Write(registry with
        {
            Team = "another-team",
            Types = [registry.Types[0] with { Team = "another-team" }]
        }), cancellationToken: Token));
    }

    [Fact]
    public void NearDuplicateNamesIdentifyTheFirstActualConflictNotAnUnrelatedPredecessor()
    {
        var error = Assert.Single(TeamRegistryRules.Validate(
            Write(Registry("cleanup", "Load-Test", "load_tests")), cancellationToken: Token));
        AssertError(error, TeamRegistryRules.DuplicateType, "$.types[2].name");
        Assert.Contains("Conflicting type: \"Load-Test\" at $.types[1].name.", error.Hint);
        Assert.DoesNotContain("cleanup", error.Hint);
    }

    [Fact]
    [RuleCoverage("WFV017", true)]
    [RuleCoverage("WFV017", false)]
    public void DefaultTypeQuotaAllowsTenAndRejectsEleven()
    {
        var registry = Registry(Enumerable.Range(0, 11).Select(index =>
            new string((char)('a' + index), 8)).ToArray());
        Assert.Empty(TeamRegistryRules.Validate(Write(registry with { Types = registry.Types[..10] }),
            cancellationToken: Token));
        var error = Assert.Single(TeamRegistryRules.Validate(Write(registry), cancellationToken: Token));
        AssertError(error, TeamRegistryRules.TypeQuota, "$.types");
        Assert.Contains("10", error.Hint);
        Assert.Empty(TeamRegistryRules.Validate(Write(registry), typeQuota: 11, cancellationToken: Token));
        AssertError(Assert.Single(TeamRegistryRules.Validate(Write(Registry("release", "cleanup")), typeQuota: 1,
            cancellationToken: Token)), TeamRegistryRules.TypeQuota, "$.types");
    }

    [Fact]
    [RuleCoverage("WFV018", true)]
    public void MetadataSchemaSupportsRequiredOptionalAllowedAndFreeFormKeys()
    {
        var registry = WithSchema(new([new("environment", true, ["prod", "dev"]), new("Environment"),
            new("note")], AllowFreeForm: true));
        Assert.Empty(TeamRegistryRules.Validate(Write(registry), cancellationToken: Token));
        var fields = JsonSerializer.Deserialize(Write(registry), TeamRegistryJsonContext.Default.TeamRegistry)!
            .Types[0].MetadataSchema.Keys;
        Assert.True(fields[0].Required);
        Assert.Empty(fields[1].AllowedValues);
        Assert.Equal(["prod", "dev"], fields[0].AllowedValues);
    }

    [Theory]
    [InlineData("""[{"key":" "}]""", "$.types[0].metadataSchema.keys[0].key")]
    [InlineData("""[{"key":"env"},{"key":"env"}]""", "$.types[0].metadataSchema.keys[1].key")]
    [InlineData("""[{"key":"env","allowedValues":["prod","prod"]}]""",
        "$.types[0].metadataSchema.keys[0].allowedValues[1]")]
    [InlineData("""[{"key":"env","allowedValues":[" "]}]""",
        "$.types[0].metadataSchema.keys[0].allowedValues[0]")]
    [InlineData("""[{"key":"env","allowedValues":[null]}]""",
        "$.types[0].metadataSchema.keys[0].allowedValues[0]")]
    [InlineData("""[null]""", "$.types[0].metadataSchema.keys[0]")]
    [RuleCoverage("WFV018", false)]
    public void InvalidMetadataSchemasHavePrecisePaths(string keys, string path) =>
        AssertError(Assert.Single(Check($$$"""
            {"team":"demo","types":[{"team":"demo","name":"release","description":"Release",
             "metadataSchema":{"keys":{{{keys}}}}}]}
            """)), TeamRegistryRules.InvalidMetadataSchema, path);

    [Fact]
    [RuleCoverage("WFV019", true)]
    [RuleCoverage("WFV019", false)]
    public void WorkflowTypeMustMatchExactlyAndBelongToThisTeam()
    {
        var utf8 = Write(Registry());
        Assert.Empty(TeamRegistryRules.ValidateWorkflow(utf8, Metadata("release"), cancellationToken: Token));
        foreach (var name in new[] { "Release", "releases", "other-team/release", "missing" })
            AssertError(Assert.Single(TeamRegistryRules.ValidateWorkflow(utf8, Metadata(name),
                cancellationToken: Token)), TeamRegistryRules.UnknownType, "$.metadata.type", "workflow.cs");
    }

    [Fact]
    [RuleCoverage("WFV020", true)]
    [RuleCoverage("WFV020", false)]
    public void WorkflowMetadataRequiredAllowedAndFreeFormConstraintsAreApplied()
    {
        var registry = WithSchema(new([new("env", true, ["prod", "dev"]), new("note")]));
        Assert.Empty(TeamRegistryRules.ValidateWorkflow(Write(registry), Metadata("release", ("env", "prod")),
            cancellationToken: Token));
        Assert.Empty(TeamRegistryRules.ValidateWorkflow(Write(registry),
            Metadata("release", ("env", "dev"), ("note", "")), cancellationToken: Token));
        foreach (var metadata in new[]
        {
            Metadata("release"), Metadata("release", ("env", " ")),
            Metadata("release", ("env", "do-not-echo")), Metadata("release", ("env", "Prod"))
        })
            AssertError(Assert.Single(TeamRegistryRules.ValidateWorkflow(Write(registry), metadata,
                cancellationToken: Token)), TeamRegistryRules.InvalidMetadata, "$.metadata.values[\"env\"]", "workflow.cs");
        AssertError(Assert.Single(TeamRegistryRules.ValidateWorkflow(Write(registry),
            Metadata("release", ("env", "prod"), ("extra", "do-not-echo")), cancellationToken: Token)),
            TeamRegistryRules.InvalidMetadata, "$.metadata.values[\"extra\"]", "workflow.cs");
        registry = WithSchema(registry.Types[0].MetadataSchema with { AllowFreeForm = true });
        Assert.Empty(TeamRegistryRules.ValidateWorkflow(Write(registry),
            Metadata("release", ("env", "prod"), ("extra", "anything")), cancellationToken: Token));
        AssertError(Assert.Single(TeamRegistryRules.ValidateWorkflow(Write(registry),
            Metadata("release", ("env", "invalid")), cancellationToken: Token)), TeamRegistryRules.InvalidMetadata,
            "$.metadata.values[\"env\"]", "workflow.cs");
    }

    [Fact]
    public void MalformedRegistrySuppressesAmbiguousWorkflowChecks()
    {
        var errors = TeamRegistryRules.ValidateWorkflow(Write(Registry("release", "release")),
            Metadata("unknown"), cancellationToken: Token);
        AssertError(Assert.Single(errors), TeamRegistryRules.DuplicateType, "$.types[1].name");
    }

    [Fact]
    public void DiagnosticsAreDeterministicImmutableAndValuesAreNotEchoed()
    {
        var registry = WithSchema(new([new("b", true), new("a", true), new("quote\"\n", true)]));
        var utf8 = Write(registry);
        var first = TeamRegistryRules.ValidateWorkflow(utf8, Metadata("release", ("extra", "do-not-echo")),
            "teams\\demo\\team.json", "workflow.cs", cancellationToken: Token);
        Assert.Equal(first, TeamRegistryRules.ValidateWorkflow(utf8, Metadata("release", ("extra", "do-not-echo")),
            "teams\\demo\\team.json", "workflow.cs", cancellationToken: Token));
        Assert.Equal(first.OrderBy(error => error.Path, StringComparer.Ordinal), first);
        Assert.Equal(4, first.Length);
        Assert.All(first, error =>
        {
            Assert.Equal("workflow.cs", error.File);
            Assert.DoesNotContain("do-not-echo", error.Message + error.Hint);
            Assert.DoesNotContain('\n', error.Path);
        });
        Assert.Equal(3, first.RemoveAt(0).Length);
        Assert.Equal(4, first.Length);
    }

    [Fact]
    public void RegistryErrorsPreserveCallerRelativeFile()
    {
        var error = Assert.Single(TeamRegistryRules.Validate(Write(Registry("release", "release")),
            "teams\\demo\\team.json", cancellationToken: Token));
        Assert.Equal("teams\\demo\\team.json", error.File);
        Assert.Equal("WFV016", error.RuleId);
    }

    [Fact]
    public void CancellationAndInvalidQuotaAreNotValidationErrors()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            TeamRegistryRules.Validate("not JSON"u8.ToArray(), cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            TeamRegistryRules.ValidateWorkflow(Write(Registry()), Metadata("release"),
                cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TeamRegistryRules.Validate(Write(Registry()), typeQuota: 0, cancellationToken: Token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TeamRegistryRules.Validate(Write(Registry()), typeQuota: -1, cancellationToken: Token));
    }

    [Fact]
    public void RealSdkManifestMetadataUsesRegistryWithoutReflectionOrTaskExecution()
    {
        var workflow = Workflow.Define("demo/release/publish", "1.0.0", workflow => workflow.Type("release")
            .Metadata("environment", "prod").Node("publish", node => node.Task("work", _ =>
                throw new InvalidOperationException("Validation must not execute tasks."))));
        var manifest = ManifestWriter.Create(workflow);
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest);
        manifest = JsonSerializer.Deserialize(utf8, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var registry = WithSchema(new([new("environment", true, ["dev", "prod"])]));
        Assert.Empty(TeamRegistryRules.ValidateWorkflow(Write(registry), manifest.Metadata, cancellationToken: Token));
        AssertError(Assert.Single(TeamRegistryRules.ValidateWorkflow(
            Write(WithSchema(new([new("environment", true, ["dev"])]))), manifest.Metadata,
            cancellationToken: Token)), TeamRegistryRules.InvalidMetadata,
            "$.metadata.values[\"environment\"]", "workflow.cs");
    }

    private static TeamRegistry Registry(params string[] names) =>
        new("demo", (names.Length == 0 ? ["release"] : names).Select(name =>
            new WorkflowTypeDefinition("demo", name, "Workflow type", new([]))).ToImmutableArray());

    private static TeamRegistry WithSchema(MetadataSchema schema)
    {
        var registry = Registry();
        return registry with { Types = [registry.Types[0] with { MetadataSchema = schema }] };
    }

    private static WorkflowMetadata Metadata(string type, params (string Key, string Value)[] values) =>
        new(type, values.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static byte[] Write(TeamRegistry registry) =>
        JsonSerializer.SerializeToUtf8Bytes(registry, TeamRegistryJsonContext.Default.TeamRegistry);

    private static ImmutableArray<ValidationError> Check(string json) =>
        TeamRegistryRules.Validate(Encoding.UTF8.GetBytes(json), cancellationToken: Token);

    private static void AssertError(ValidationError error, ValidationRule rule, string path, string file = "team.json")
    {
        Assert.Equal(rule.Id, error.RuleId);
        Assert.Equal(file, error.File);
        Assert.Equal(path, error.Path);
        Assert.Equal(rule.Message, error.Message);
        Assert.StartsWith(rule.Hint, error.Hint);
        Assert.DoesNotContain("do-not-echo", error.Message + error.Hint);
    }
}
