using System.Collections.Immutable;
using Wf.Validation;
using Wf.Validation.Tests;

namespace Wf.Integration.Tests.Definition;

[Collection("Validator")]
public sealed class SelectorManifestTests
{
    [Fact]
    public async Task Actual_file_app_publish_and_describe_preserve_ids_labels_count_os_and_scope_overrides()
    {
        using var fixture = new ValidatorFixture();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        fixture.WriteWorkflow($$"""
            var workflow = Workflow.Define("demo/test/selector", "1.0.0", w => w.Type("test")
                .Variables(v => v.String("pool", "ci"))
                .RunsOn(Machines.Ids(Guid.Parse("{{first:D}}"), Guid.Parse("{{second:D}}")), Machines.Count(2), Os.Linux)
                .Node("ids", n => n.Task("inherited", _ => throw new Exception("Describe must not execute tasks.")))
                .Node("labels", n => n.RunsOn(v => Machines.All(Machines.Label("role", "perf"),
                        Machines.Pool(v["pool"]), Machines.Count(3), Os.Windows))
                    .Task("inherited", _ => throw new Exception("Describe must not execute tasks."))
                    .Task("override", _ => throw new Exception("Describe must not execute tasks."),
                        t => t.RunsOn(Machines.Ids(Guid.Parse("{{second:D}}"))))));
            await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
            """);
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.NotNull(result.Build);
        var manifest = result.Build.Manifest;
        var resolved = PlacementResolver.Resolve(manifest.Nodes,
            ImmutableDictionary<string, object>.Empty.Add("pool", "ci"));
        var ids = resolved[0].Tasks[0].Placement!.Selector!;
        Assert.Equal([first, second], ids.MachineIds);
        Assert.Equal(2, ids.Count);
        Assert.Equal("linux", ids.Os!.Literal);
        var labels = resolved[1].Tasks[0].Placement!.Selector!;
        Assert.Empty(labels.MachineIds);
        Assert.Equal(3, labels.Count);
        Assert.Equal("windows", labels.Os!.Literal);
        Assert.Equal("ci", labels.Pool!.Literal);
        Assert.Equal("perf", Assert.Single(labels.Labels).Value!.Literal);
        var task = resolved[1].Tasks[1].Placement!.Selector!;
        Assert.Equal([second], task.MachineIds);
        Assert.Null(task.Count);
        Assert.Null(task.Os);
        Assert.Null(task.Pool);
        Assert.Empty(task.Labels);
        Assert.Equal("pool", manifest.Nodes[1].Tasks[0].Placement!.Selector!.Pool!.Variable);
        var cached = await fixture.Cache.BuildAsync(new(fixture.Script, fixture.Source, fixture.AdditionalInputs),
            ValidatorFixture.Token);
        Assert.True(cached.CacheHit);
        Assert.Equal(result.Build.CacheKey, cached.CacheKey);
        Assert.Equal([first, second], cached.Manifest.Nodes[0].Tasks[0].Placement!.Selector!.MachineIds);
    }
}
