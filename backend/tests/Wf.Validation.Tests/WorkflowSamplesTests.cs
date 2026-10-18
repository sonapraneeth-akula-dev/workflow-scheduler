using System.Text.Json;
using Wf.Contracts.Registry;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

[Collection("Validator")]
public sealed class WorkflowSamplesTests
{
    [Theory]
    [InlineData("demo", "Demo", 3, 9)]
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
}
