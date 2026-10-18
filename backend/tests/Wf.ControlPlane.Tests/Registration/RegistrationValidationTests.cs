using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.ControlPlane.Registration;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Storage;
using Wf.Validation;
using Wf.Validation.Rules;

namespace Wf.ControlPlane.Tests.Registration;

public sealed class RegistrationValidationTests
{
    private const string InvalidSchedule = """
        {"instances":[{"id":"daily","cron":"* * * * *","timezone":"Etc/UTC","variables":{"unknown":1}}]}
        """;
    private const string UnknownType = """
        {"team":"team","types":[{"team":"team","name":"other","description":"Other","metadataSchema":{"keys":[]}}]}
        """;
    private static WorkflowManifest Manifest => new("1.0.0", "team/release/workflow", "1.0.0",
        new("release", ImmutableSortedDictionary<string, string>.Empty), [], [], [], TimeSpan.FromHours(1));

    [Theory]
    [InlineData("{}")]
    [InlineData(UnknownType)]
    public async Task RegistrationValidation_aggregates_schedule_and_registry_errors_before_any_storage(string registry)
    {
        await using var data = DataSource();
        var service = new RegistrationService(data, new UnusedBlobs(), new RegistryWriter(), TimeProvider.System, new("prod"));
        var request = Request() with
        {
            Registry = Parse(registry), Schedules = InvalidSchedule, ScheduleSourcePath = "schedules.json"
        };
        var expected = Validator.Validate(Manifest, Encoding.UTF8.GetBytes(InvalidSchedule), Encoding.UTF8.GetBytes(registry),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, expected.Errors.Length);
        Assert.Equal("WFV013", expected.Errors[0].RuleId);
        Assert.StartsWith("WFV01", expected.Errors[1].RuleId);
        var actual = await Assert.ThrowsAsync<RegistrationValidationException>(() =>
            service.RegisterAsync(request, "actor", TestContext.Current.CancellationToken));
        Assert.Equal(expected.Errors, actual.Errors);
    }

    [Fact]
    public async Task RegistrationValidation_raw_semantics_precede_companion_errors_and_contract_construction()
    {
        await using var data = DataSource();
        var service = new RegistrationService(data, new UnusedBlobs(), new RegistryWriter(), TimeProvider.System, new("prod"));
        var node = JsonNode.Parse(Request().Manifest!.Value.GetRawText())!;
        node["variables"] = JsonNode.Parse("""
            [{"key":"count","type":"Int","default":"never-echo-this-value","required":false}]
            """);
        var raw = Parse(node.ToJsonString());
        var expected = ManifestRules.Validate(raw, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(expected);
        var error = await Assert.ThrowsAsync<RegistrationValidationException>(() =>
            service.RegisterAsync(Request() with
            {
                Manifest = raw, Registry = Parse("{}"), Schedules = InvalidSchedule, ScheduleSourcePath = "schedules.json"
            }, "actor", TestContext.Current.CancellationToken));
        Assert.Equal(expected, error.Errors);
        Assert.DoesNotContain("never-echo-this-value", JsonSerializer.Serialize(error.Errors));
    }

    [Fact]
    public async Task RegistrationValidation_registry_only_keeps_shared_rules()
    {
        await using var data = DataSource();
        var service = new RegistrationService(data, new UnusedBlobs(), new RegistryWriter(), TimeProvider.System, new("prod"));
        var request = new RegistrationRequest(new('a', 40), "Author", Registry: Parse("{}"));
        var error = await Assert.ThrowsAsync<RegistrationValidationException>(() =>
            service.RegisterAsync(request, "actor", TestContext.Current.CancellationToken));
        Assert.Equal(TeamRegistryRules.Validate("{}"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken), error.Errors);
    }

    [Fact]
    public async Task RegistrationValidation_cancellation_is_not_a_problem_or_storage_attempt()
    {
        await using var data = DataSource();
        var service = new RegistrationService(data, new UnusedBlobs(), new RegistryWriter(), TimeProvider.System, new("prod"));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RegisterAsync(Request(), "actor", cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Validator.Validate(Manifest, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void RegistrationValidation_manifest_only_has_no_build_side_effect_and_checks_trusted_quota()
    {
        var result = Validator.Validate(Manifest, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsValid);
        Assert.Null(result.Build);
        Assert.Null(result.BuildFailure);
        Assert.Throws<ArgumentOutOfRangeException>(() => Validator.Validate(Manifest, options: new(0),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => Validator.Validate((WorkflowManifest)null!,
            cancellationToken: TestContext.Current.CancellationToken));
        var labelled = Validator.Validate(Manifest, Encoding.UTF8.GetBytes(InvalidSchedule),
            scheduleFile: "teams/team/release/workflow/schedules.json", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("teams/team/release/workflow/schedules.json", Assert.Single(labelled.Errors).File);
    }

    [Fact]
    public void RegistrationValidation_typed_manifest_errors_precede_companions_without_duplicate_schema_exceptions()
    {
        var manifest = Manifest with
        {
            Variables = [new("duplicate", VariableType.String), new("duplicate", VariableType.String)]
        };
        var result = Validator.Validate(manifest, Encoding.UTF8.GetBytes(InvalidSchedule), "{}"u8.ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ManifestRules.Validate(manifest, cancellationToken: TestContext.Current.CancellationToken), result.Errors);
        Assert.Equal("WFV007", Assert.Single(result.Errors).RuleId);
    }

    [Fact]
    public async Task RegistrationValidation_absent_companions_are_not_empty_files()
    {
        await using var data = DataSource();
        var service = new RegistrationService(data, new UnusedBlobs(), new RegistryWriter(), TimeProvider.System, new("prod"));
        var error = await Assert.ThrowsAsync<RegistrationValidationException>(() =>
            service.RegisterAsync(Request(), "actor", TestContext.Current.CancellationToken));
        var diagnostic = Assert.Single(error.Errors);
        Assert.Equal("REG001", diagnostic.RuleId);
        Assert.Equal("$.package", diagnostic.Path);
    }

    private static RegistrationRequest Request() => new(new('a', 40), "Author", Manifest.Name,
        JsonSerializer.SerializeToElement(Manifest, WorkflowManifestJsonContext.Default.WorkflowManifest));

    [Theory]
    [InlineData("dev", "Production")]
    [InlineData("prod", "Demo")]
    public async Task Wrong_deployment_is_rejected_before_storage(string environment, string deployment)
    {
        await using var data = DataSource();
        var service = new RegistrationService(data, new UnusedBlobs(), new RegistryWriter(), TimeProvider.System, new(environment));
        var registry = Parse($$"""{"team":"team","types":[],"deployment":"{{deployment}}"}""");
        var request = new RegistrationRequest(new('a', 40), "Author", Registry: registry);
        var error = await Assert.ThrowsAsync<RegistrationValidationException>(() =>
            service.RegisterAsync(request, "actor", TestContext.Current.CancellationToken));
        Assert.Equal("$.registry.deployment", Assert.Single(error.Errors).Path);
    }
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static NpgsqlDataSource DataSource() => NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");

    private sealed class UnusedBlobs : IBlobStore
    {
        public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must precede blob writes.");
        public Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation must precede blob reads.");
    }
}
