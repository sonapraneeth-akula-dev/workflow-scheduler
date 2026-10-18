using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Versions;

public enum ManifestChangeKind { Added, Removed, Changed }
public sealed record ManifestChange(string Key, ManifestChangeKind Kind, JsonElement? Before, JsonElement? After);
public sealed record ManifestChanges(ManifestChange[] Nodes, ManifestChange[] Variables,
    ManifestChange[] Policies, ManifestChange[] Settings);
public sealed record WorkflowSourceDiff(string Path, string Before, string After, bool Changed);
public sealed record WorkflowVersionDiff(string WorkflowId, Guid FromVersionId, Guid ToVersionId,
    string FromVersion, string ToVersion, ManifestChanges Manifest, WorkflowSourceDiff Source);

public static class ManifestDiff
{
    public static ManifestChanges Compare(WorkflowManifest before, WorkflowManifest after)
    {
        if (before.Name != after.Name) throw new ArgumentException("Compare versions of the same workflow.");
        var left = JsonSerializer.SerializeToElement(before, WorkflowManifestJsonContext.Default.WorkflowManifest);
        var right = JsonSerializer.SerializeToElement(after, WorkflowManifestJsonContext.Default.WorkflowManifest);
        return new(Changes(Keyed(left, "nodes"), Keyed(right, "nodes")),
            Changes(Keyed(left, "variables"), Keyed(right, "variables")),
            Changes(Fields(left, ["runTimeout", "placement"]), Fields(right, ["runTimeout", "placement"])),
            Changes(Fields(left, ["sdkVersion", "metadata", "stages"]), Fields(right, ["sdkVersion", "metadata", "stages"])));
    }

    private static Dictionary<string, JsonElement> Keyed(JsonElement manifest, string property) =>
        manifest.GetProperty(property).EnumerateArray().ToDictionary(
            entry => entry.GetProperty("key").GetString() ?? throw new InvalidDataException(),
            entry => entry, StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> Fields(JsonElement manifest, string[] properties) =>
        properties.Where(property => manifest.TryGetProperty(property, out _))
            .ToDictionary(property => property, manifest.GetProperty, StringComparer.Ordinal);

    private static ManifestChange[] Changes(Dictionary<string, JsonElement> before, Dictionary<string, JsonElement> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(key =>
        {
            JsonElement? left = before.TryGetValue(key, out var oldValue) ? oldValue : null;
            JsonElement? right = after.TryGetValue(key, out var newValue) ? newValue : null;
            return left is { } old && right is { } next && JsonElement.DeepEquals(old, next) ? null :
                new ManifestChange(key, left is null ? ManifestChangeKind.Added :
                    right is null ? ManifestChangeKind.Removed : ManifestChangeKind.Changed, left, right);
        }).OfType<ManifestChange>().ToArray();
}

public static class VersionSourceReader
{
    public const int MaximumSourceBytes = 1024 * 1024;
    public const int MaximumArchiveBytes = MaximumSourceBytes + 1024;

    public static async Task<string> ReadAsync(IBlobStore store, BlobContent content, CancellationToken token)
    {
        if (content.Kind != BlobKind.Sources || content.SizeBytes is <= 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("Invalid workflow source archive bounds.");
        using var compressed = new MemoryStream(checked((int)content.SizeBytes));
        await store.DownloadAsync(content, compressed, token);
        compressed.Position = 0;
        using var archive = new ZipArchive(compressed, ZipArchiveMode.Read);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "workflow.cs" ||
            archive.Entries[0].Length is <= 0 or > MaximumSourceBytes)
            throw new InvalidDataException("Invalid workflow source archive layout.");
        var entry = archive.Entries[0];
        using var source = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        await source.ReadExactlyAsync(bytes, token);
        if (await source.ReadAsync(new byte[1], token) != 0)
            throw new InvalidDataException("Workflow source expands beyond its declared length.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
