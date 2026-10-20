using System.Text.Json;
using Wf.Contracts.Registry;
using Wf.Validation.Rules;
using Wf.Validation.Schedules;

namespace Wf.Validation.Tests;

[Collection("Validator")]
public sealed class WorkflowSamplesTests
{
    [Theory]
    [InlineData("demo", "Demo", 4, 11)]
    [InlineData("production", "Production", 1, 2)]
    public void Source_tree_ownership_deployment_and_project_references_match_the_registry(
        string tree, string deployment, int teams, int workflows)
    {
        using var fixture = new ValidatorFixture();
        var root = Samples(fixture);
        var registries = Directory.GetFiles(Path.Combine(root, tree, "teams"), "team.json", SearchOption.AllDirectories);
        Assert.Equal(teams, registries.Length);
        var count = 0;
        foreach (var path in registries)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Empty(TeamRegistryRules.Validate(bytes, cancellationToken: ValidatorFixture.Token));
            var registry = JsonSerializer.Deserialize(bytes, TeamRegistryJsonContext.Default.TeamRegistry)!;
            var directory = Path.GetDirectoryName(path)!;
            Assert.Equal(Path.GetFileName(directory), registry.Team);
            Assert.Equal(deployment, registry.Deployment);
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(directory, registry.Schema!))));
            foreach (var script in Directory.GetFiles(directory, "workflow.cs", SearchOption.AllDirectories))
            {
                var workflow = Path.GetDirectoryName(script)!;
                var type = Path.GetFileName(Path.GetDirectoryName(workflow));
                Assert.Contains(registry.Types, definition => definition.Name == type);
                var source = File.ReadAllText(script);
                var identity = $"{registry.Team}/{type}/{Path.GetFileName(workflow)}";
                Assert.Contains($"Workflow.Define(\"{identity}\",", source);
                var reference = File.ReadLines(script).First()["#:project ".Length..];
                Assert.True(File.Exists(Path.GetFullPath(Path.Combine(workflow, reference))));
                count++;
            }
        }
        Assert.Equal(workflows, count);
    }

    [Theory]
    [InlineData("readiness", "configuration-check")]
    [InlineData("maintenance", "capacity-preview")]
    public async Task Production_templates_compile_and_validate_without_registration_or_task_execution(string type, string workflow)
    {
        using var fixture = new ValidatorFixture();
        var directory = Path.Combine(Samples(fixture), "production", "teams", "operations", type, workflow);
        var result = await Validator.ValidateAsync(directory, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)) +
            result.BuildFailure?.StandardOutput + result.BuildFailure?.StandardError);
        Assert.NotNull(result.Build);
        Assert.Equal($"operations/{type}/{workflow}", result.Build.Manifest.Name);
        Assert.Equal(type, result.Build.Manifest.Metadata.Type);
        Assert.Equal("ppe", result.Build.Manifest.Metadata.Values["environment"]);
    }

    private static string Samples(ValidatorFixture fixture) =>
        Path.GetFullPath(Path.Combine(fixture.AdditionalInputs[0], "..", "..", "samples", "workflows"));

    [Fact]
    public async Task Housing_profiles_override_experiment_variables_and_alternate_every_two_hours()
    {
        using var fixture = new ValidatorFixture();
        var directory = Path.Combine(Samples(fixture), "demo", "teams", "data-science", "regression", "linear-regression");
        var result = await Validator.ValidateAsync(directory, fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)) +
            result.BuildFailure?.StandardOutput + result.BuildFailure?.StandardError);
        Assert.NotNull(result.Build);
        Assert.Equal("data-science/regression/linear-regression", result.Build.Manifest.Name);
        Assert.Equal("regression", result.Build.Manifest.Metadata.Type);
        var schedules = ScheduleFileValidator.Validate(
            await File.ReadAllBytesAsync(Path.Combine(directory, "schedules.json"), ValidatorFixture.Token),
            result.Build.Manifest.Variables, cancellationToken: ValidatorFixture.Token);
        Assert.True(schedules.IsValid);
        Assert.Equal(2, schedules.Instances.Length);
        var training = Assert.Single(schedules.Instances, item => item.Definition.Id == "daily-training-validation");
        var full = Assert.Single(schedules.Instances, item => item.Definition.Id == "daily-full-test");
        Assert.False(training.Variables["runFinalTest"].GetBoolean());
        Assert.True(full.Variables["runFinalTest"].GetBoolean());
        Assert.Equal(50, training.Variables["epochs"].GetInt32());
        Assert.Equal(100, full.Variables["epochs"].GetInt32());
        Assert.Equal(1, training.Variables["experimentNumber"].GetInt32());
        Assert.Equal(2, full.Variables["experimentNumber"].GetInt32());
        Assert.Equal(1, Assert.Single(result.Build.Manifest.Variables, variable => variable.Key == "experimentNumber").Default!.Value.GetInt32());
        var now = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero), training.Expression.GetNextOccurrence(now));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero), full.Expression.GetNextOccurrence(now));
        foreach (var profile in schedules.Instances)
        {
            Assert.Equal("Etc/UTC", profile.Definition.Timezone);
            Assert.Equal(Wf.Contracts.Schedules.ScheduleOverlapPolicy.Skip, profile.Definition.Overlap);
            Assert.Equal(Wf.Contracts.Schedules.ScheduleCatchUpPolicy.Latest, profile.Definition.CatchUp);
            Assert.Null(profile.Definition.VersionPin);
            Assert.False(profile.Variables["forceDownload"].GetBoolean());
            Assert.Equal(70, profile.Variables["trainingPercent"].GetInt32());
            Assert.Equal(15, profile.Variables["validationPercent"].GetInt32());
            Assert.Equal(15, profile.Variables["testPercent"].GetInt32());
            Assert.Equal(0.1, profile.Variables["learningRate"].GetDouble());
            Assert.Equal(8, profile.Variables.Count);
            Assert.Equal(profile.Expression.GetNextOccurrence(now)!.Value.AddHours(4),
                profile.Expression.GetNextOccurrence(profile.Expression.GetNextOccurrence(now)!.Value));
        }
        var cursor = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 12; index++)
        {
            var nextTraining = training.Expression.GetNextOccurrence(cursor)!.Value;
            var nextFull = full.Expression.GetNextOccurrence(cursor)!.Value;
            var next = nextTraining < nextFull ? nextTraining : nextFull;
            Assert.Equal(cursor.AddHours(2), next);
            Assert.Equal(index % 2 == 0, nextFull < nextTraining);
            cursor = next;
        }
    }
}
