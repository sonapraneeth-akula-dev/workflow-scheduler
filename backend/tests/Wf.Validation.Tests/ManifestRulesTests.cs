using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk.Testing;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class ManifestRulesTests
{
    [Fact]
    [RuleCoverage("WFV005", true)]
    [RuleCoverage("WFV006", true)]
    public void AcyclicGraphAndKnownDependenciesAreValid() =>
        Assert.Empty(Check("""{"nodes":[{"key":"first"},{"key":"second","dependencies":[{"node":"first"}]}]}"""));

    [Theory]
    [InlineData("""{"nodes":[{"key":"a","dependencies":[{"node":"a"}]}]}""")]
    [InlineData("""{"nodes":[{"key":"a","dependencies":[{"node":"b"}]},{"key":"b","dependencies":[{"node":"a"}]}]}""")]
    [RuleCoverage("WFV005", false)]
    public void CyclesHaveStableStructuredErrors(string json) =>
        AssertError(Assert.Single(Check(json)), "WFV005", "$.nodes");

    [Fact]
    [RuleCoverage("WFV006", false)]
    public void UnknownDependencyPointsToItsReference() =>
        AssertError(Assert.Single(Check("""{"nodes":[{"key":"a","dependencies":[{"node":"missing"}]}]}""")),
            "WFV006", "$.nodes[0].dependencies[0].node");

    [Fact]
    [RuleCoverage("WFV007", true)]
    public void KeysAreOrdinalAndTasksAreScopedToTheirNode() =>
        Assert.Empty(Check("""
            {"nodes":[{"key":"a","tasks":[{"key":"work"}]},{"key":"A","tasks":[{"key":"work"}]}],
             "variables":[{"key":"a","type":"String"},{"key":"A","type":"Int"}]}
            """));

    [Theory]
    [InlineData("""{"nodes":[{"key":"a"},{"key":"a"}]}""", "$.nodes[1].key")]
    [InlineData("""{"nodes":[{"key":"a","tasks":[{"key":"work"},{"key":"work"}]}]}""", "$.nodes[0].tasks[1].key")]
    [InlineData("""{"variables":[{"key":"a","type":"Int"},{"key":"a","type":"Int"}]}""", "$.variables[1].key")]
    [InlineData("""{"nodes":[{"key":"a"},{"key":"b","dependencies":[{"node":"a"},{"node":"a"}]}]}""",
        "$.nodes[1].dependencies[1].node")]
    [InlineData("""{"variables":[{"key":"a","type":"String","type":"Int"}]}""", "$.variables[0].type")]
    [RuleCoverage("WFV007", false)]
    public void DuplicateKeysHaveScopedPaths(string json, string path) =>
        AssertError(Assert.Single(Check(json)), "WFV007", path);

    [Theory]
    [InlineData("String", "\"value\"")]
    [InlineData("Int", "42")]
    [InlineData("Long", "9223372036854775807")]
    [InlineData("Double", "1.25")]
    [InlineData("Bool", "false")]
    [InlineData("Enum", "\"Dev\"")]
    [InlineData("StringList", "[\"a\",\"b\"]")]
    [InlineData("Duration", "\"1.02:03:04\"")]
    [InlineData("Secret", "{\"Name\":\"deploy-token\"}")]
    [RuleCoverage("WFV008", true)]
    public void AllVariableTypesReuseContractValidation(string type, string value) =>
        Assert.Empty(Check(Variable(type, value)));

    [Theory]
    [InlineData("String", "1")]
    [InlineData("Int", "\"1\"")]
    [InlineData("Int", "2147483648")]
    [InlineData("Long", "9223372036854775808")]
    [InlineData("Double", "1e999")]
    [InlineData("Bool", "\"false\"")]
    [InlineData("Enum", "\"Other\"")]
    [InlineData("StringList", "[\"a\",null]")]
    [InlineData("Duration", "\"five minutes\"")]
    [InlineData("Secret", "{\"Name\":\"token\",\"Value\":\"do-not-echo\"}")]
    [RuleCoverage("WFV008", false)]
    public void WrongDefaultsHaveAPathAndNeverEchoValues(string type, string value) =>
        AssertError(Assert.Single(Check(Variable(type, value))), "WFV008", "$.variables[0].default");

    [Theory]
    [InlineData("""{"key":"a","type":"Unknown"}""", "type")]
    [InlineData("""{"key":"a","type":99}""", "type")]
    [InlineData("""{"key":"a","type":null}""", "type")]
    [InlineData("""{"key":"a","type":"Enum","allowedValues":[]}""", "allowedValues")]
    [InlineData("""{"key":"a","type":"Int","allowedValues":["1"]}""", "allowedValues")]
    [InlineData("""{"key":"a","type":"String","allowedValues":["a","a"]}""", "allowedValues")]
    [InlineData("""{"key":"a","type":"String","allowedValues":[null]}""", "allowedValues")]
    [InlineData("""{"key":"a","type":"String","allowedValues":1}""", "allowedValues")]
    public void InvalidVariableDeclarationsAreCheckedBeforeConstructingContracts(string variable, string property) =>
        AssertError(Assert.Single(Check($$"""{"variables":[{{variable}}]}""")),
            "WFV008", "$.variables[0]." + property);

    [Theory]
    [InlineData("""{"key":"a","type":"String","required":true}""")]
    [InlineData("""{"key":"a","type":"Int","required":true,"default":3}""")]
    [InlineData("""{"key":"a","type":"String","required":true,"default":null}""")]
    [InlineData("""{"key":"a","type":"String","required":false}""")]
    [RuleCoverage("WFV009", true)]
    public void RequiredSchemasMayBeSuppliedAtRunTimeOrHaveDefaults(string variable) =>
        Assert.Empty(Check($$"""{"variables":[{{variable}}]}"""));

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [RuleCoverage("WFV009", false)]
    public void RequiredMustBeBoolean(string required) =>
        AssertError(Assert.Single(Check($$"""{"variables":[{"key":"a","type":"String","required":{{required}}}]}""")),
            "WFV009", "$.variables[0].required");

    [Fact]
    public void ErrorsAreDeterministicAndPreserveCallerRelativeFile()
    {
        const string json = """
            {"nodes":[{"key":"a","dependencies":[{"node":"missing"}],"tasks":[{"key":"t"},{"key":"t"}]},
                      {"key":"a"}],
             "variables":[{"key":"x","type":"Int","default":"do-not-echo","required":1},
                          {"key":"x","type":"Unknown"}]}
            """;
        var first = Check(json, "generated/manifest.json");
        Assert.Equal(first, Check(json, "generated/manifest.json"));
        Assert.Equal(["WFV006", "WFV007", "WFV007", "WFV007", "WFV008", "WFV008", "WFV009"],
            first.Select(error => error.RuleId));
        Assert.All(first, error =>
        {
            Assert.Equal("generated/manifest.json", error.File);
            Assert.StartsWith("$.", error.Path);
            Assert.DoesNotContain("do-not-echo", error.Message + error.Hint);
            Assert.DoesNotContain(Path.GetPathRoot(Directory.GetCurrentDirectory())!, error.Message);
        });
    }

    [Fact]
    public void TypedManifestUsesTheSameRulesAsRawManifest()
    {
        var manifest = new WorkflowManifest("1.0.0", "test", "1.0.0", new("release",
            ImmutableSortedDictionary<string, string>.Empty), [], [],
            [new("a", [new("missing")], [])], TimeSpan.FromHours(1));
        var raw = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest);
        Assert.Equal(ManifestRules.Validate(raw, cancellationToken: ValidatorFixture.Token),
            ManifestRules.Validate(manifest, cancellationToken: ValidatorFixture.Token));
        AssertError(Assert.Single(ManifestRules.Validate(manifest, cancellationToken: ValidatorFixture.Token)),
            "WFV006", "$.nodes[0].dependencies[0].node");
    }

    [Fact]
    public void CancellationAndMalformedJsonAreNotValidationErrors()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var document = JsonDocument.Parse("{}");
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ManifestRules.Validate(document.RootElement, cancellationToken: cancelled.Token));
        Assert.ThrowsAny<JsonException>(() => Check("not JSON"));
    }

    private static string Variable(string type, string value) =>
        $$"""{"variables":[{"key":"input","type":"{{type}}","default":{{value}},"allowedValues":{{(type == "Enum" ? "[\"Dev\",\"Prod\"]" : "[]")}}}]}""";

    private static ImmutableArray<ValidationError> Check(string json, string file = "workflow.cs") =>
        CheckWithToken(json, file, ValidatorFixture.Token);

    private static ImmutableArray<ValidationError> CheckWithToken(string json, string file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json);
        return ManifestRules.Validate(document.RootElement, file, cancellationToken);
    }

    private static void AssertError(ValidationError error, string id, string path)
    {
        var rule = Assert.Single(RuleCatalog.All, rule => rule.Id == id);
        Assert.Equal(new(id, "workflow.cs", path, rule.Message, rule.Hint), error);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
        Assert.DoesNotContain("do-not-echo", error.Message + error.Hint);
    }
}

[Collection("Validator")]
public sealed class ManifestRulesIntegrationTests
{
    [Fact]
    public async Task CompiledSdkManifestIsCheckedByBothValidatorAndTestingFacade()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow(ValidatorFixture.ValidDefinition.Replace(
            "await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());", """
                var manifest = System.Text.Json.Nodes.JsonNode.Parse(ManifestWriter.Write(workflow))!;
                manifest["nodes"]![1]!["dependencies"]![0]!["node"] = "missing";
                Console.WriteLine(manifest.ToJsonString());
                """, StringComparison.Ordinal));
        var direct = await Validator.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        var sync = Validator.Validate(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        var facade = await WorkflowValidation.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        var syncFacade = WorkflowValidation.Validate(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.False(direct.IsValid);
        Assert.Equal(RuleCatalog.UnknownDependency.CreateError("workflow.cs", "$.nodes[1].dependencies[0].node"),
            Assert.Single(direct.Errors));
        Assert.Equal(direct.Errors, sync.Errors);
        Assert.Equal(direct.Errors, facade.Errors);
        Assert.Equal(direct.Errors, syncFacade.Errors);
        Assert.Null(direct.BuildFailure);
    }

    [Fact]
    public async Task CompiledSdkInvalidVariableIsRejectedBeforeItsContractConstructor()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow(ValidatorFixture.ValidDefinition.Replace(
            "await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());", """
                var manifest = System.Text.Json.Nodes.JsonNode.Parse(ManifestWriter.Write(workflow))!;
                manifest["variables"]![0]!["default"] = 42;
                Console.WriteLine(manifest.ToJsonString());
                """, StringComparison.Ordinal));
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.False(result.IsValid);
        Assert.Equal(RuleCatalog.VariableType.CreateError("workflow.cs", "$.variables[0].default"),
            Assert.Single(result.Errors));
        Assert.Null(result.BuildFailure);
    }

    [Fact]
    public async Task CompiledSdkRequiredWithoutDefaultIsValidAndTasksAreNotExecuted()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow(ValidatorFixture.ValidDefinition.Replace(".String(\"environment\", \"dev\")",
            ".String(\"environment\", required: true)", StringComparison.Ordinal));
        var result = await WorkflowValidation.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.True(result.IsValid);
        Assert.NotNull(result.Build);
        Assert.True(Assert.Single(result.Build.Manifest.Variables).Required);
        Assert.Null(Assert.Single(result.Build.Manifest.Variables).Default);
    }
}
