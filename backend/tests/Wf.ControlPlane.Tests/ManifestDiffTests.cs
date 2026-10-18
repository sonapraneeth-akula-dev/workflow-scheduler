using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Storage;
using Wf.ControlPlane.Versions;

namespace Wf.ControlPlane.Tests;

public sealed class ManifestDiffTests
{
    [Fact]
    public void ManifestDiff_reports_added_removed_and_changed_nodes_variables_and_policies_in_ordinal_order()
    {
        var before = Manifest() with
        {
            Nodes = [Node("removed"), Node("changed"), Node("same")],
            Variables = [new("removed", VariableType.Int), new("changed", VariableType.String), new("same", VariableType.Bool)]
        };
        var after = before with
        {
            Version = "2.0.0", RunTimeout = TimeSpan.FromHours(2),
            Placement = new(new(pool: new(literal: "pool"))),
            Nodes = [Node("same"), new("changed", [], [new("task")], timeout: TimeSpan.FromMinutes(2)), Node("added")],
            Variables = [new("same", VariableType.Bool), new("changed", VariableType.String, required: true),
                new("added", VariableType.Long)]
        };
        var diff = ManifestDiff.Compare(before, after);
        foreach (var changes in new[] { diff.Nodes, diff.Variables })
        {
            Assert.Equal(["added", "changed", "removed"], changes.Select(change => change.Key));
            Assert.Equal([ManifestChangeKind.Added, ManifestChangeKind.Changed, ManifestChangeKind.Removed],
                changes.Select(change => change.Kind));
            Assert.Null(changes[0].Before);
            Assert.NotNull(changes[0].After);
            Assert.NotNull(changes[1].Before);
            Assert.NotNull(changes[1].After);
            Assert.NotNull(changes[2].Before);
            Assert.Null(changes[2].After);
        }
        Assert.Equal(["placement", "runTimeout"], diff.Policies.Select(change => change.Key));
        Assert.Equal(ManifestChangeKind.Added, diff.Policies[0].Kind);
        Assert.Empty(diff.Settings);
        Assert.Equal("00:02:00", diff.Nodes[1].After!.Value.GetProperty("timeout").GetString());
        Assert.True(diff.Variables[1].After!.Value.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void ManifestDiff_is_bidirectional_and_ignores_version_labels_and_top_level_keyed_array_order()
    {
        var before = Manifest() with
        {
            Nodes = [Node("b"), Node("a")],
            Variables = [new("b", VariableType.Int), new("a", VariableType.String)]
        };
        var after = before with { Version = "1.0.1", Nodes = [.. before.Nodes.Reverse()], Variables = [.. before.Variables.Reverse()] };
        var same = ManifestDiff.Compare(before, after);
        Assert.Empty(same.Nodes);
        Assert.Empty(same.Variables);
        Assert.Empty(same.Policies);
        Assert.Empty(same.Settings);
        var changed = after with { Nodes = [Node("new")], RunTimeout = TimeSpan.FromHours(2) };
        var forward = ManifestDiff.Compare(before, changed);
        var reverse = ManifestDiff.Compare(changed, before);
        Assert.Equal(forward.Nodes.Select(change => change.Key), reverse.Nodes.Select(change => change.Key));
        foreach (var (left, right) in forward.Nodes.Zip(reverse.Nodes))
        {
            Assert.Equal(left.Before?.GetRawText(), right.After?.GetRawText());
            Assert.Equal(left.After?.GetRawText(), right.Before?.GetRawText());
        }
        Assert.Throws<ArgumentException>(() => ManifestDiff.Compare(before, changed with { Name = "team/type/foreign" }));
        Assert.Throws<ArgumentException>(() => ManifestDiff.Compare(before, changed with { Nodes = [Node("a"), Node("a")] }));
    }

    [Fact]
    public void ManifestDiff_detects_task_action_policy_default_metadata_and_stage_changes()
    {
        var before = Manifest() with
        {
            Nodes = [new("work", [], [new("task", policy: new(TimeSpan.FromSeconds(10), new()),
                action: new(TaskActionKind.Command, "dotnet"))])],
            Variables = [new("count", VariableType.Int, @default: JsonSerializer.SerializeToElement(1))]
        };
        var task = new TaskDefinition("task", policy: new(TimeSpan.FromSeconds(20), new(2)),
            action: new(TaskActionKind.Command, "node"));
        var after = before with
        {
            Nodes = [new("work", [], [task])],
            Variables = [new("count", VariableType.Int, @default: JsonSerializer.SerializeToElement(2))],
            Stages = ["$init", "$cleanup"], SdkVersion = "2",
            Metadata = new("type", ImmutableSortedDictionary<string, string>.Empty.Add("key", "value"))
        };
        var diff = ManifestDiff.Compare(before, after);
        Assert.Single(diff.Nodes);
        Assert.Single(diff.Variables);
        Assert.Equal(["metadata", "sdkVersion", "stages"], diff.Settings.Select(change => change.Key));
        var encoded = JsonSerializer.Serialize(new WorkflowVersionDiff(before.Name, Guid.CreateVersion7(),
            Guid.CreateVersion7(), "1.0.0", "2.0.0", diff, new("workflow.cs", "old\r\n", "new\n", true)),
            VersionsJsonContext.Default.WorkflowVersionDiff);
        Assert.Contains("\"kind\":\"Changed\"", encoded);
        Assert.Contains("\"before\":\"old\\r\\n\"", encoded);
        Assert.DoesNotContain("sourceUri", encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?from=x&to=y")]
    [InlineData("?from=00000000-0000-0000-0000-000000000000&to=00000000-0000-0000-0000-000000000000")]
    [InlineData("?From=0199e9f8-7f00-7000-8000-000000000001&to=0199e9f8-7f00-7000-8000-000000000002")]
    [InlineData("?from=0199e9f8-7f00-7000-8000-000000000001&to=0199e9f8-7f00-7000-8000-000000000002&limit=1")]
    [InlineData("?from=0199e9f8-7f00-7000-8000-000000000001&from=0199e9f8-7f00-7000-8000-000000000001&to=0199e9f8-7f00-7000-8000-000000000002")]
    [InlineData("?from=0199E9F8-7F00-7000-8000-000000000001&to=0199e9f8-7f00-7000-8000-000000000002")]
    public void ManifestDiff_rejects_ambiguous_or_noncanonical_version_pairs(string query) =>
        Assert.Throws<ArgumentException>(() => WorkflowVersionPair.Parse(new QueryCollection(
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query))));

    [Fact]
    public void ManifestDiff_allows_same_version_and_explicit_reverse_pairs()
    {
        var from = Guid.CreateVersion7();
        var to = Guid.CreateVersion7();
        WorkflowVersionPair Pair(Guid a, Guid b) => WorkflowVersionPair.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { ["from"] = a.ToString("D"), ["to"] = b.ToString("D") }));
        Assert.Equal(new(from, to), Pair(from, to));
        Assert.Equal(new(to, from), Pair(to, from));
        Assert.Equal(new(from, from), Pair(from, from));
    }

    [Theory]
    [InlineData("source\r\nUnicode: \u03bb\n")]
    [InlineData("\uFEFFfirst line\nlast line")]
    public async Task ManifestDiff_source_reader_preserves_exact_UTF8_text(string text)
    {
        var zip = Archive(("workflow.cs", Encoding.UTF8.GetBytes(text)));
        var store = new MemoryStore(zip);
        Assert.Equal(text, await VersionSourceReader.ReadAsync(store, Content(zip), TestContext.Current.CancellationToken));
        Assert.Equal(1, store.Reads);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("traversal")]
    [InlineData("directory")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("invalid-utf8")]
    [InlineData("expanded")]
    [InlineData("invalid-zip")]
    public async Task ManifestDiff_source_reader_rejects_invalid_layout_encoding_and_expansion(string kind)
    {
        var zip = kind switch
        {
            "empty" => Archive(("workflow.cs", [])),
            "traversal" => Archive(("../workflow.cs", [1])),
            "directory" => Archive(("nested/workflow.cs", [1])),
            "extra" => Archive(("workflow.cs", [1]), ("extra", [2])),
            "duplicate" => Archive(("workflow.cs", [1]), ("workflow.cs", [2])),
            "invalid-utf8" => Archive(("workflow.cs", [0xff, 0xfe])),
            "expanded" => Archive(("workflow.cs", new byte[VersionSourceReader.MaximumSourceBytes + 1])),
            _ => new byte[] { 1, 2, 3 }
        };
        if (kind == "invalid-utf8")
            await Assert.ThrowsAsync<DecoderFallbackException>(() =>
                VersionSourceReader.ReadAsync(new MemoryStore(zip), Content(zip), TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
                VersionSourceReader.ReadAsync(new MemoryStore(zip), Content(zip), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManifestDiff_source_reader_accepts_the_full_registration_text_ceiling()
    {
        var text = new string('x', VersionSourceReader.MaximumSourceBytes);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using var entry = archive.CreateEntry("workflow.cs", CompressionLevel.NoCompression).Open();
            entry.Write(Encoding.UTF8.GetBytes(text));
        }
        var zip = output.ToArray();
        Assert.True(zip.Length <= VersionSourceReader.MaximumArchiveBytes);
        Assert.Equal(text, await VersionSourceReader.ReadAsync(new MemoryStore(zip), Content(zip), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManifestDiff_source_reader_enforces_archive_bounds_before_storage_and_propagates_cancellation()
    {
        var store = new MemoryStore([]);
        foreach (var size in new long[] { 0, VersionSourceReader.MaximumArchiveBytes + 1L })
            await Assert.ThrowsAsync<InvalidDataException>(() => VersionSourceReader.ReadAsync(store,
                new(BlobKind.Sources, new('a', 64), size), TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Reads);
        var zip = Archive(("workflow.cs", [(byte)'x']));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VersionSourceReader.ReadAsync(new MemoryStore(zip), Content(zip), cancelled.Token));
    }

    private static WorkflowManifest Manifest() => new("1", "team/type/diff", "1.0.0",
        new("type", ImmutableSortedDictionary<string, string>.Empty), [], [], [], TimeSpan.FromHours(1));
    private static NodeDefinition Node(string key) => new(key, [], [new("task")]);
    private static BlobContent Content(byte[] zip) => new(BlobKind.Sources,
        Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length);

    private static byte[] Archive(params (string Name, byte[] Content)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                using var output = zip.CreateEntry(entry.Name, CompressionLevel.SmallestSize).Open();
                output.Write(entry.Content);
            }
        return bytes.ToArray();
    }

    private sealed class MemoryStore(byte[] bytes) : IBlobStore
    {
        public int Reads { get; private set; }
        public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            await destination.WriteAsync(bytes, cancellationToken);
        }
    }
}
