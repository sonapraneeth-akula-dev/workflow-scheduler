using System.Text.Json;
using Wf.Contracts.Manifest;
using Wf.Validation.SemVer;

namespace Wf.Validation.Tests;

[Collection("Validator")]
public sealed class BreakingChangeClassifierIntegrationTests
{
    [Fact]
    public async Task RealSdkOldAndNewManifestsClassifyChangesAndRejectWrongVersionsWithRequiredHints()
    {
        using var fixture = new ValidatorFixture();
        var token = ValidatorFixture.Token;
        fixture.WriteWorkflow("""
            if (System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault)
                throw new InvalidOperationException("Reflection must be disabled.");
            WorkflowDefinition Define(string version, string change) =>
                Workflow.Define("semver/integration", version, w =>
                {
                    w.Type("release").Metadata("implementation", change);
                    w.Variables(v =>
                    {
                        if (change != "variable-removed")
                            v.String(change == "variable-renamed" ? "target" : "environment",
                                change == "default-changed" ? "prod" : "dev",
                                allowed: change == "allowed-narrowed" ? ["dev"] : ["dev", "prod"]);
                        if (change == "type-changed")
                            v.Long("count", 3);
                        else
                            v.Int("count", 3);
                        v.String("optional", required: change == "required-tightened");
                        if (change is "variable-added" or "required-added" or "defaulted-required-added")
                            v.String("extra", change == "defaulted-required-added" ? "fallback" : null,
                                required: change != "variable-added");
                    });
                    w.Node("prepare", n => n.Task("work", _ =>
                        throw new InvalidOperationException("Must not execute tasks.")));
                    if (change != "node-removed")
                        w.Node(change == "node-renamed" ? "release" : "deploy", n =>
                        {
                            if (change != "implementation")
                                n.DependsOn("prepare");
                            n.Task(change == "implementation" ? "replaced-body" : "work", _ =>
                                throw new InvalidOperationException("Must not execute tasks."));
                        });
                    if (change == "node-added")
                        w.Node("audit", n => n.Task("work", _ =>
                            throw new InvalidOperationException("Must not execute tasks.")));
                });
            foreach (var change in new[] { "implementation", "node-removed", "node-renamed", "node-added",
                "variable-removed", "variable-renamed", "type-changed", "variable-added", "required-added",
                "defaulted-required-added", "required-tightened", "default-changed", "allowed-narrowed" })
                foreach (var version in new[] { "1.2.4", "1.3.0", "2.0.0" })
                {
                    await using var file = File.Create(Path.Combine("..", $"breaking-change-{change}-{version}.json"));
                    await ManifestWriter.WriteAsync(Define(version, change), file);
                }
            await ManifestWriter.WriteAsync(Define("1.2.3", "old"), Console.OpenStandardOutput());
            """);
        var script = await File.ReadAllTextAsync(fixture.Script, token);
        await File.WriteAllTextAsync(fixture.Script, script.Replace("#:property PublishAot=false",
            "#:property PublishAot=false\n#:property JsonSerializerIsReflectionEnabledByDefault=false",
            StringComparison.Ordinal), token);
        var validation = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs, token);
        Assert.True(validation.IsValid,
            $"{validation.BuildFailure?.StandardOutput}\n{validation.BuildFailure?.StandardError}\n{string.Join("\n", validation.Errors)}");
        var old = ApiBaseline.FromManifest(Assert.IsType<Wf.Validation.Build.ScriptBuildResult>(validation.Build).Manifest);
        var cases = new (string Change, SemVerBump Bump, ApiChangeKind? Kind)[]
        {
            ("implementation", SemVerBump.Patch, null),
            ("node-removed", SemVerBump.Major, ApiChangeKind.NodeRemoved),
            ("node-renamed", SemVerBump.Major, ApiChangeKind.NodeRemoved),
            ("node-added", SemVerBump.Minor, ApiChangeKind.NodeAdded),
            ("variable-removed", SemVerBump.Major, ApiChangeKind.VariableRemoved),
            ("variable-renamed", SemVerBump.Major, ApiChangeKind.VariableRemoved),
            ("type-changed", SemVerBump.Major, ApiChangeKind.VariableTypeChanged),
            ("variable-added", SemVerBump.Minor, ApiChangeKind.VariableAdded),
            ("required-added", SemVerBump.Major, ApiChangeKind.VariableAdded),
            ("defaulted-required-added", SemVerBump.Minor, ApiChangeKind.VariableAdded),
            ("required-tightened", SemVerBump.Major, ApiChangeKind.VariableRequiredChanged),
            ("default-changed", SemVerBump.Major, ApiChangeKind.VariableDefaultChanged),
            ("allowed-narrowed", SemVerBump.Major, ApiChangeKind.VariableAllowedValuesChanged)
        };
        foreach (var (change, bump, kind) in cases)
            foreach (var (version, actual) in new[] {
                ("1.2.4", SemVerBump.Patch), ("1.3.0", SemVerBump.Minor), ("2.0.0", SemVerBump.Major) })
            {
                var bytes = await File.ReadAllBytesAsync(
                    Path.Combine(fixture.Root, $"breaking-change-{change}-{version}.json"), token);
                var manifest = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
                var current = ApiBaseline.Parse(ApiBaseline.FromManifest(manifest).WriteUtf8());
                var result = BreakingChangeClassifier.Classify(old, current);

                Assert.Equal(bump, result.RequiredBump);
                Assert.Equal(actual >= bump, result.IsValid);
                if (kind is { } expectedKind)
                    Assert.Contains(result.Changes, entry => entry.Kind == expectedKind);
                else
                    Assert.Empty(result.Changes);
                if (result.IsValid)
                    Assert.Empty(result.Errors);
                else
                {
                    var error = Assert.Single(result.Errors);
                    Assert.Equal(BreakingChangeClassifier.VersionBumpRuleId, error.RuleId);
                    Assert.Equal(ApiBaseline.FileName, error.File);
                    Assert.Equal("$.version", error.Path);
                    Assert.Contains(bump.ToString().ToUpperInvariant(), error.Hint);
                }
            }
    }
}
