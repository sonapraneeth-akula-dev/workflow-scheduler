using System.Text;
using System.Text.Json;
using Wf.Contracts.Manifest;

namespace Wf.Cli.Publishing;

internal sealed record PublishInputs(string? Source, string? RegistryText, string? Schedules)
{
    internal JsonElement? Registry
    {
        get
        {
            if (RegistryText is null) return null;
            using var document = JsonDocument.Parse(RegistryText);
            return document.RootElement.Clone();
        }
    }

    internal static async Task<PublishInputs> ReadAsync(string directory, string? schedulePath, CancellationToken token)
    {
        var script = Path.Combine(directory, "workflow.cs");
        var source = File.Exists(script) ? await TextAsync(script, token) : null;
        var scheduleFile = Path.Combine(directory, "schedules.json");
        var schedules = File.Exists(scheduleFile) ? await TextAsync(scheduleFile, token) : null;
        if (schedules is not null && (schedulePath is null || schedules.Contains('\0')))
            throw new ArgumentException("schedules.json requires --schedule-source-path and must not contain NUL characters.");
        if (schedules is null && schedulePath is not null)
            throw new ArgumentException("--schedule-source-path requires a local schedules.json.");
        string? registry = null;
        string? current = directory;
        for (var level = 0; current is not null && level <= 2; current = Path.GetDirectoryName(current), level++)
        {
            var candidate = Path.Combine(current, "team.json");
            if (Directory.Exists(candidate)) break;
            if (!File.Exists(candidate)) continue;
            registry = await TextAsync(candidate, token);
            break;
        }
        return new(source, registry, schedules);
    }

    internal void ValidateIdentity(WorkflowManifest manifest)
    {
        var parts = manifest.Name.Split('/');
        if (parts.Length != 3 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
            part.EnumerateRunes().Take(129).Count() > 128 || part.Any(character => character is '\\' || char.IsControl(character))) ||
            manifest.Metadata.Type != parts[1] || manifest.Version.Length > 256)
            throw new InvalidDataException("Manifest requires an exact team/type/name identity matching metadata.type and a version of at most 256 characters.");
        if (Source is null) throw new InvalidDataException("Validated workflow.cs source is missing.");
        if (Registry is { } registry && registry.GetProperty("team").GetString() != parts[0])
            throw new InvalidDataException("team.json ownership must match the manifest's team/type/name identity.");
    }

    internal static string FullPath(string path)
    {
        if (path.Length > 4096 || path.Any(char.IsControl))
            throw new ArgumentException("Local paths must be at most 4096 characters without control characters.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            Uri.TryCreate(path, UriKind.Absolute, out var uri) && (!uri.IsFile || uri.IsUnc))
            throw new ArgumentException("Publish accepts local filesystem paths, not URLs or UNC shares.");
        var full = Path.GetFullPath(path);
        RejectLinks(full);
        return full;
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Publish inputs and work paths cannot contain symbolic links or reparse points.");
    }

    internal static async Task<byte[]> ReadFileAsync(string path, int maximum, CancellationToken token)
    {
        RejectLinks(path);
        await using var stream = File.OpenRead(path);
        if (stream.Length is 0 || stream.Length > maximum)
            throw new InvalidDataException($"Publish input must contain 1 byte to {maximum} bytes.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, token) != 0)
            throw new IOException("Publish input changed while reading.");
        return bytes;
    }

    private static async Task<string> TextAsync(string path, CancellationToken token)
    {
        var bytes = await ReadFileAsync(path, 1024 * 1024, token);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Publish inputs must contain valid UTF-8 text."); }
    }
}
