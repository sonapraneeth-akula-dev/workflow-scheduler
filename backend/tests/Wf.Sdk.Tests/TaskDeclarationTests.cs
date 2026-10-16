using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class TaskDeclarationTests
{
    [Fact]
    public void Manifest_records_original_sync_async_and_helper_declarations_without_captured_values()
    {
        var workflow = Workflow.Define("team/type/plan", "1.0.0", builder => builder.Type("type")
            .Node("work", node => node.Task("sync", DeclaredSync).Task("async", DeclaredAsync)
                .Task("command", Tasks.Command("dotnet", "--token captured-secret",
                    environment: new Dictionary<string, string?> { ["TOKEN"] = "environment-secret" }))
                .Task("http", Tasks.Http(HttpMethod.Post,
                    "https://user:password@example.com/jobs?token=query-secret#fragment-secret",
                    headers: new Dictionary<string, string> { ["X-Token"] = "header-secret" }, body: "body-secret"))));
        var bytes = ManifestWriter.WriteUtf8(workflow);
        var roundtrip = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var tasks = Assert.Single(roundtrip.Nodes).Tasks;
        Assert.Equal(TaskActionKind.SdkFunction, tasks[0].Action!.Kind);
        Assert.EndsWith(".DeclaredSync", tasks[0].Action!.Declaration);
        Assert.EndsWith(".DeclaredAsync", tasks[1].Action!.Declaration);
        Assert.Equal(new(TaskActionKind.Command, "dotnet"), tasks[2].Action);
        Assert.Equal(new(TaskActionKind.Http, "POST https://example.com/jobs"), tasks[3].Action);
        var json = Encoding.UTF8.GetString(bytes);
        foreach (var secret in new[] { "captured-secret", "environment-secret", "password", "query-secret",
            "fragment-secret", "header-secret", "body-secret" })
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Equal(bytes, ManifestWriter.WriteUtf8(workflow));
    }

    [Fact]
    public void Legacy_task_contract_without_declaration_roundtrips_without_inventing_an_action()
    {
        var task = new TaskDefinition("legacy");
        var manifest = new WorkflowManifest("1.0.0", "team/type/plan", "1.0.0",
            new("type", System.Collections.Immutable.ImmutableSortedDictionary<string, string>.Empty),
            [], [], [new("work", [], [task])], TimeSpan.FromHours(1));
        var json = JsonSerializer.Serialize(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest);
        Assert.DoesNotContain("\"action\"", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize(json, WorkflowManifestJsonContext.Default.WorkflowManifest)!.Nodes[0].Tasks[0].Action);
    }

    [Fact]
    public void Declarations_reject_unknown_kind_empty_oversized_and_control_characters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskAction((TaskActionKind)99, "f"));
        foreach (var value in new[] { "", " ", "f\nvalue", new string('x', 4097) })
            Assert.ThrowsAny<ArgumentException>(() => new TaskAction(TaskActionKind.SdkFunction, value));
    }

    private static void DeclaredSync(TaskContext _) => throw new InvalidOperationException("Description must not execute.");
    private static Task DeclaredAsync(TaskContext _) => throw new InvalidOperationException("Description must not execute.");
}
