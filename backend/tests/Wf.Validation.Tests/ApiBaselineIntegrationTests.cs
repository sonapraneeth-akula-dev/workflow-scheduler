using System.Text.Json;
using Wf.Contracts.Manifest;
using Wf.Validation.SemVer;

namespace Wf.Validation.Tests;

[Collection("Validator")]
public sealed class ApiBaselineIntegrationTests
{
    [Fact]
    public async Task RealSdkManifestRoundTripsToBaselineWithoutReflectionOrTaskExecution()
    {
        using var fixture = new ValidatorFixture();
        var token = ValidatorFixture.Token;
        fixture.WriteWorkflow("""
            if (System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault)
                throw new InvalidOperationException("Reflection must be disabled in this fixture.");
            var workflow = Workflow.Define("baseline/integration", "2.1.0-rc.1+build.42", w => w
                .Type("release").Metadata("area", "not-public-api")
                .Variables(v => v.String("environment", "dev", required: true, allowed: ["prod", "dev"])
                    .Int("count", 3).Secret("token", "deploy-token", required: true)
                    .StringList("regions", ["west", "east"]).Duration("timeout", TimeSpan.FromMinutes(1)))
                .SystemInit((Action<TaskContext>)(_ =>
                    throw new InvalidOperationException("Must not execute lifecycle tasks.")))
                .Node("prepare", n => n.Task("work", _ =>
                    throw new InvalidOperationException("Must not execute tasks.")))
                .Node("deploy", n => n.DependsOn("prepare").Input("prepare", "version")
                    .Task("work", _ => throw new InvalidOperationException("Must not execute tasks."))));
            var baseline = Wf.Validation.SemVer.ApiBaseline.FromManifest(ManifestWriter.Create(workflow));
            await using (var file = File.Create(Path.Combine("..", Wf.Validation.SemVer.ApiBaseline.FileName)))
                await baseline.WriteAsync(file);
            await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
            """);
        var script = await File.ReadAllTextAsync(fixture.Script, token);
        await File.WriteAllTextAsync(fixture.Script, script.Replace("#:property PublishAot=false",
            "#:property PublishAot=false\n#:property JsonSerializerIsReflectionEnabledByDefault=false",
            StringComparison.Ordinal), token);
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, token);
        Assert.True(result.IsValid,
            $"{result.BuildFailure?.StandardOutput}\n{result.BuildFailure?.StandardError}\n{string.Join("\n", result.Errors)}");
        var build = Assert.IsType<Wf.Validation.Build.ScriptBuildResult>(result.Build);
        var manifest = JsonSerializer.Deserialize(build.ManifestUtf8.Span,
            WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var path = Path.Combine(fixture.Root, ApiBaseline.FileName);
        await using var input = File.OpenRead(path);
        var baseline = await ApiBaseline.ReadAsync(input, token);
        Assert.Equal(manifest.Version, baseline.Version);
        Assert.Equal(manifest.Nodes.Select(node => node.Key).Order(StringComparer.Ordinal), baseline.Nodes);
        Assert.Equal(manifest.Variables.Select(variable => variable.Key).Order(StringComparer.Ordinal),
            baseline.Variables.Select(variable => variable.Key));
        foreach (var expected in manifest.Variables)
        {
            var actual = Assert.Single(baseline.Variables, variable => variable.Key == expected.Key);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Required, actual.Required);
            Assert.Equal(expected.AllowedValues.Order(StringComparer.Ordinal), actual.AllowedValues);
            Assert.Equal(expected.Default is null, actual.Default is null);
            if (expected.Default is { } value)
                Assert.True(JsonElement.DeepEquals(value, actual.Default!.Value));
        }
        Assert.Equal(ApiBaseline.FromManifest(manifest).WriteUtf8(), baseline.WriteUtf8());
        Assert.Equal(await File.ReadAllBytesAsync(path, token), baseline.WriteUtf8());
        Assert.Contains("$init", baseline.Nodes);
    }
}
