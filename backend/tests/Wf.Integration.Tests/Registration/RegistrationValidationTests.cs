using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wf.Validation;
using Wf.Validation.Tests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    public async Task RegistrationValidation_actual_sdk_offline_and_http_errors_are_identical_and_no_state_changes()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow($$"""
            var workflow = Workflow.Define("{{request.WorkflowId}}", "1.0.0", w => w
                .Type("release").Node("work", n => n.Task("task", _ =>
                    throw new InvalidOperationException("Task execution is forbidden during validation."))));
            await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
            """);
        var registryPath = Path.Combine(fixture.Source, "team.json");
        var schedulePath = Path.Combine(fixture.Source, "schedules.json");
        await File.WriteAllTextAsync(registryPath, request.Registry!.Value.GetRawText(), ValidatorFixture.Token);
        var valid = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.True(valid.IsValid);
        Assert.NotNull(valid.Build);
        using var manifest = JsonDocument.Parse(valid.Build.ManifestUtf8);
        request = request with { Manifest = manifest.RootElement.Clone(), Source = await File.ReadAllTextAsync(fixture.Script, ValidatorFixture.Token) };
        using var created = await server.Post(request);
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            await created.Content.ReadAsStringAsync(ValidatorFixture.Token));
        var version = (await Json(created)).GetProperty("workflowVersion").GetGuid();
        const string schedules = """
            {"instances":[{"id":"daily","cron":"* * * * *","timezone":"Etc/UTC","variables":{"unknown":1}}]}
            """;
        foreach (var registry in new[]
                 {
                     request.Registry.Value.GetRawText().Replace("\"release\"", "\"other\"", StringComparison.Ordinal),
                     "{}",
                     request.Registry.Value.GetRawText()
                 })
        {
            await File.WriteAllTextAsync(registryPath, registry, ValidatorFixture.Token);
            await File.WriteAllTextAsync(schedulePath, schedules, ValidatorFixture.Token);
            var offline = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
            Assert.False(offline.IsValid);
            Assert.Contains(offline.Errors, error => error.RuleId == "WFV013");
            using var rawRegistry = JsonDocument.Parse(registry);
            using var response = await server.Post(request with
            {
                Registry = rawRegistry.RootElement.Clone(), Schedules = schedules, ScheduleSourcePath = "schedules.json"
            });
            await AssertRegistrationValidationErrors(response, offline);
            Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
            Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(1L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(request)));
            Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
            Assert.Equal(0L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(0L, await Value("SELECT count(*) FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        }

        await File.WriteAllTextAsync(registryPath, request.Registry.Value.GetRawText(), ValidatorFixture.Token);
        await File.WriteAllTextAsync(schedulePath, """{"instances":[]}""", ValidatorFixture.Token);
        Assert.True((await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token)).IsValid);
        using var corrected = await server.Post(request with { Schedules = """{"instances":[]}""", ScheduleSourcePath = "schedules.json" });
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.Equal("scheduleConfigVersion", Assert.Single((await Json(corrected)).EnumerateObject()).Name);
    }

    [Fact]
    public async Task RegistrationValidation_raw_offline_semantic_errors_are_not_masked_by_invalid_companions()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        var raw = JsonNode.Parse(request.Manifest!.Value.GetRawText())!;
        raw["variables"] = JsonNode.Parse("""
            [{"key":"count","type":"Int","default":"not-an-integer","required":false}]
            """);
        var json = raw.ToJsonString();
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow($"Console.WriteLine({JsonSerializer.Serialize(json)});");
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "team.json"), "{}", ValidatorFixture.Token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "schedules.json"), "{}", ValidatorFixture.Token);
        var offline = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.False(offline.IsValid);
        Assert.Null(offline.Build);
        Assert.Contains(offline.Errors, error => error.RuleId == "WFV008");
        using var document = JsonDocument.Parse(json);
        using var registry = JsonDocument.Parse("{}");
        using var response = await server.Post(request with
        {
            Manifest = document.RootElement.Clone(), Registry = registry.RootElement.Clone(),
            Schedules = "{}", ScheduleSourcePath = "schedules.json"
        });
        await AssertRegistrationValidationErrors(response, offline);
        Assert.Equal(0L, await Value("SELECT count(*) FROM teams WHERE id=$1", Team(request)));
        Assert.Equal(0L, await Value("SELECT count(*) FROM workflows WHERE id=$1", request.WorkflowId!));
    }

    private static async Task AssertRegistrationValidationErrors(HttpResponseMessage response, ValidationResult offline)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await Json(response);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        var errors = problem.GetProperty("errors").EnumerateArray().Select(error => new ValidationError(
            error.GetProperty("ruleId").GetString()!, error.GetProperty("file").GetString()!,
            error.GetProperty("path").GetString()!, error.GetProperty("message").GetString()!,
            error.GetProperty("hint").GetString()!)).ToArray();
        Assert.Equal(offline.Errors, errors);
    }
}
