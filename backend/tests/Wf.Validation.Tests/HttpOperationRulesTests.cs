using System.Text.Json;
using System.Text.Json.Nodes;
using Wf.Contracts.Manifest;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class HttpOperationRulesTests
{
    private const string Path = "$.nodes[0].tasks[0]";

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 0, true)]
    public void NeitherRecoveryContractRequiresBothNoAutoRetryAndZeroAuthorRetries(bool automatic, int retries, bool valid)
    {
        var root = Manifest();
        Task(root)["policy"]!["autoRetry"] = automatic;
        Task(root)["policy"]!["retry"]!["retries"] = retries;
        var errors = Check(root);
        if (valid) Assert.Empty(errors);
        else Assert.Equal(HttpOperationRules.UnsafeRetry.CreateError("workflow.cs", Path + ".policy"), Assert.Single(errors));
    }

    [Fact]
    public void OmittedEffectivePolicyRetainsUnsafePlatformRetryDefault()
    {
        var root = Manifest();
        Task(root).AsObject().Remove("policy");
        Assert.Equal("WFV022", Assert.Single(Check(root)).RuleId);
    }

    [Theory]
    [InlineData("deduplication", """{"headerName":"Idempotency-Key","retention":"2.00:00:00"}""")]
    [InlineData("lookup", """{"urlTemplate":"https://example.test/lookup/{submissionKey}","retention":"2.00:00:00","authoritative":true}""")]
    public void EitherGuaranteedDeduplicationOrAuthoritativeLookupAllowsRetries(string name, string value)
    {
        var root = Manifest();
        Operation(root)[name] = JsonNode.Parse(value);
        Assert.Empty(Check(root));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("deduplication")]
    [InlineData("lookup")]
    public void AllDeclaredRetentionWindowsMustCoverRunDeadlineWhenRetriesAreEnabled(string shortened)
    {
        var root = Manifest();
        Operation(root)["deduplication"] = JsonNode.Parse("""{"headerName":"Idempotency-Key","retention":"2.00:00:00"}""");
        Operation(root)["lookup"] = JsonNode.Parse("""
            {"urlTemplate":"https://example.test/lookup/{submissionKey}","retention":"2.00:00:00","authoritative":true}
            """);
        Operation(root)[shortened]!["retention"] = "00:30:00";
        Assert.Equal(HttpOperationRules.InsufficientRetention.CreateError("workflow.cs", Path + ".httpOperation"),
            Assert.Single(Check(root)));
        Task(root)["policy"]!["autoRetry"] = false;
        Assert.Empty(Check(root));
    }

    [Theory]
    [InlineData("submissionUrl", "\"https://user:do-not-echo@example.test/jobs\"")]
    [InlineData("submissionUrl", "\"https://example.test/jobs?token=do-not-echo\"")]
    [InlineData("submissionUrl", "\"relative-do-not-echo\"")]
    [InlineData("submissionUrl", "null")]
    [InlineData("status", "null")]
    [InlineData("status.stateField", "[]")]
    [InlineData("status.locationHeader", "\"bad\\r\\nheader\"")]
    [InlineData("status.retention", "\"00:00:00\"")]
    [InlineData("status.retention", "\"366.00:00:00\"")]
    [InlineData("status.running", "\"Pending\"")]
    [InlineData("status.urlTemplate", "\"https://other.test/jobs/{operationId}\"")]
    [InlineData("requestTimeout", "\"00:00:00\"")]
    [InlineData("requestTimeout", "\"00:00:31\"")]
    [InlineData("secretReference", "\"do-not-echo\\nsecret\"")]
    [InlineData("secretReference", "\"secret://vault/../do-not-echo\"")]
    [InlineData("trustedOrigins", "[\"https://example.test/path\"]")]
    [InlineData("cancel", """{"urlTemplate":"https://other.test/jobs/{operationId}","requestTimeout":"00:00:10"}""")]
    [InlineData("cancel", """{"urlTemplate":"{statusUrl}/cancel","requestTimeout":"00:00:11"}""")]
    [InlineData("cancel", """{"urlTemplate":"{statusUrl}/cancel","method":"Get","requestTimeout":"00:00:10"}""")]
    [InlineData("deduplication", """{"headerName":"Cookie","retention":"2.00:00:00"}""")]
    [InlineData("lookup", """{"urlTemplate":"https://example.test/lookup/{submissionKey}","retention":"2.00:00:00","authoritative":false}""")]
    [InlineData("lookup", """{"urlTemplate":"https://example.test/lookup/{unknown}","retention":"2.00:00:00"}""")]
    [InlineData("do-not-echo", "\"secret\"")]
    public void ForgedRawContractsReturnStableSanitizedErrorsBeforeConstruction(string property, string value)
    {
        var root = Manifest();
        var target = Operation(root);
        var segments = property.Split('.');
        foreach (var segment in segments[..^1])
            target = target[segment]!;
        target[segments[^1]] = JsonNode.Parse(value);
        AssertSanitized(Assert.Single(Check(root)), HttpOperationRules.InvalidContract, Path + ".httpOperation");
    }

    [Theory]
    [InlineData("stateField", 129)]
    [InlineData("locationHeader", 129)]
    [InlineData("pending", 129)]
    public void RawFieldBoundsMatchAuthoringBoundary(string property, int length)
    {
        var root = Manifest();
        Operation(root)["status"]![property] = new string('x', length);
        AssertSanitized(Assert.Single(Check(root)), HttpOperationRules.InvalidContract, Path + ".httpOperation");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"autoRetry":"false"}""")]
    [InlineData("""{"retry":{"retries":"0"}}""")]
    [InlineData("""{"retry":{"retries":-1}}""")]
    [InlineData("""{"retry":{"retries":11}}""")]
    public void InvalidRetryPolicyCannotBypassSafetyRule(string policy)
    {
        var root = Manifest();
        Task(root)["policy"] = JsonNode.Parse(policy);
        AssertSanitized(Assert.Single(Check(root)), HttpOperationRules.InvalidContract, Path + ".policy");
    }

    [Fact]
    public void DuplicateAndUnknownRemotePropertiesDoNotLeakPropertyNamesInPaths()
    {
        using var document = JsonDocument.Parse("""
            {"nodes":[{"key":"node","tasks":[{"key":"task","httpOperation":{"do-not-echo":1,"do-not-echo":2}}]}]}
            """);
        AssertSanitized(Assert.Single(ManifestRules.Validate(document.RootElement, cancellationToken: ValidatorFixture.Token)),
            HttpOperationRules.InvalidContract, Path + ".httpOperation");
    }

    [Fact]
    public void SourceGeneratedTypedAndRawRulesAgreeAndNullOperationsRemainOrdinaryTasks()
    {
        var root = Manifest();
        Task(root)["policy"]!["autoRetry"] = false;
        var typed = JsonSerializer.Deserialize(root.ToJsonString(), WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.Equal(Check(root), HttpOperationRules.Validate(typed, cancellationToken: ValidatorFixture.Token));
        Assert.Empty(ManifestRules.Validate(typed, cancellationToken: ValidatorFixture.Token));
        Task(root)["httpOperation"] = null;
        Task(root)["policy"]!["autoRetry"] = true;
        Assert.Empty(Check(root));
    }

    [Fact]
    public void CancellationIsNotReportedAsAContractError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var document = JsonDocument.Parse("{}");
        Assert.ThrowsAny<OperationCanceledException>(() =>
            HttpOperationRules.Validate(document.RootElement, cancellationToken: cancellation.Token));
    }

    private static JsonNode Manifest() => JsonNode.Parse("""
        {"sdkVersion":"1.0.0","name":"remote","version":"1.0.0","metadata":{"type":"test","values":{}},
         "variables":[],"stages":[],"runTimeout":"1.00:00:00",
         "nodes":[{"key":"node","dependencies":[],"tasks":[{"key":"task",
          "policy":{"attemptTimeout":"01:00:00","autoRetry":true,"retry":{"retries":0}},
          "httpOperation":{"submissionUrl":"https://example.test/jobs","requestTimeout":"00:00:30",
           "status":{"stateField":"state","retention":"2.00:00:00","locationHeader":"Location"}}}]}]}
        """)!;

    private static JsonNode Task(JsonNode root) => root["nodes"]![0]!["tasks"]![0]!;
    private static JsonNode Operation(JsonNode root) => Task(root)["httpOperation"]!;
    private static System.Collections.Immutable.ImmutableArray<ValidationError> Check(JsonNode root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return ManifestRules.Validate(document.RootElement, cancellationToken: ValidatorFixture.Token);
    }

    private static void AssertSanitized(ValidationError error, ValidationRule rule, string path)
    {
        Assert.Contains(rule, RuleCatalog.All);
        Assert.Equal(rule.CreateError("workflow.cs", path), error);
        Assert.DoesNotContain("do-not-echo", error.ToString());
    }
}

[Collection("Validator")]
public sealed class HttpOperationValidatorIntegrationTests
{
    [Theory]
    [InlineData("", "WFV022")]
    [InlineData(".Retry(0)", "WFV022")]
    [InlineData(".NoAutoRetry()", null)]
    [InlineData(".NoAutoRetry().Retry(0)", null)]
    [InlineData(".NoAutoRetry().Retry(1)", "WFV022")]
    public async Task CompiledSdkManifestEnforcesBothRetrySwitches(string policy, string? rule)
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow($$"""
            var workflow = Workflow.Define("http-operation", "1.0.0", w => w.Type("release")
                .Node("jobs", n => n.Task("submit",
                    Tasks.HttpOperation("https://must-not-contact.invalid/jobs",
                        new Wf.Contracts.Definition.HttpOperationStatusContract("state", TimeSpan.FromDays(2))),
                    p => { p{{policy}}; })));
            await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
            """.Replace("p => { p; }", "p => { }", StringComparison.Ordinal));
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.Null(result.BuildFailure);
        Assert.Equal(rule is null, result.IsValid);
        if (rule is not null)
            Assert.Equal(rule, Assert.Single(result.Errors).RuleId);
        else
        {
            var task = Assert.Single(Assert.Single(result.Build!.Manifest.Nodes).Tasks);
            Assert.NotNull(task.HttpOperation);
            Assert.False(task.Policy.AutoRetry);
            Assert.Equal(0, task.Policy.Retry.Retries);
        }
    }

    [Fact]
    public async Task CompiledSdkCarriesDedupAndLookupAndTaskOverrideCanResetInheritedRetries()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow("""
            var status = new Wf.Contracts.Definition.HttpOperationStatusContract("state", TimeSpan.FromDays(2));
            var workflow = Workflow.Define("http-operation", "1.0.0", w => w.Type("release")
                .Defaults(p => p.Retry(2))
                .Node("jobs", n => n
                    .Task("no-retry", Tasks.HttpOperation("https://example.test/jobs", status),
                        p => p.NoAutoRetry().Retry(0))
                    .Task("dedup", Tasks.HttpOperation("https://example.test/jobs", status,
                        deduplication: new("Idempotency-Key", TimeSpan.FromDays(2))))
                    .Task("lookup", Tasks.HttpOperation("https://example.test/jobs", status,
                        lookup: new("https://example.test/lookup/{submissionKey}", TimeSpan.FromDays(2))))));
            await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
            """);
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.Null(result.BuildFailure);
        Assert.Empty(result.Errors);
        var tasks = Assert.Single(result.Build!.Manifest.Nodes).Tasks;
        Assert.Equal(0, tasks[0].Policy.Retry.Retries);
        Assert.False(tasks[0].Policy.AutoRetry);
        Assert.Equal(2, tasks[1].Policy.Retry.Retries);
        Assert.NotNull(tasks[1].HttpOperation!.Deduplication);
        Assert.True(tasks[2].HttpOperation!.Lookup!.Authoritative);
    }

    [Fact]
    public async Task CompiledSdkForgedRemoteContractIsRejectedBeforeDeserializationWithoutValueLeaks()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow("""
            var workflow = Workflow.Define("http-operation", "1.0.0", w => w.Type("release")
                .Node("jobs", n => n.Task("submit",
                    Tasks.HttpOperation("https://example.test/jobs",
                        new Wf.Contracts.Definition.HttpOperationStatusContract("state", TimeSpan.FromDays(2))),
                    p => p.NoAutoRetry().Retry(0))));
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(ManifestWriter.Write(workflow))!;
            manifest["nodes"]![0]!["tasks"]![0]!["httpOperation"]!["submissionUrl"] =
                "https://example.test/jobs?token=do-not-echo";
            Console.WriteLine(manifest.ToJsonString());
            """);
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.Null(result.BuildFailure);
        Assert.Equal(HttpOperationRules.InvalidContract.CreateError("workflow.cs", "$.nodes[0].tasks[0].httpOperation"),
            Assert.Single(result.Errors));
        Assert.DoesNotContain("do-not-echo", result.Errors[0].ToString());
    }
}
