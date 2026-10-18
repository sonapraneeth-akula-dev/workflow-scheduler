using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Validation.Build;

namespace Wf.Cli.Packaging;

public sealed record PackageBuildResult(string Path, string Sha256, long Size);

/// <summary>
/// Packages a verified successful ScriptBuildCache publish without compiling again. All publish files are
/// retained. Identical published bytes/names and version produce identical ZIP bytes: ordinal paths,
/// fixed ZIP epoch/attributes, no compression (no compressor-version dependence). Does not normalize
/// nondeterministic compiler output, native dependencies, ambient inputs or differences between SDKs/OSes.
/// </summary>
public static class PackageBuilder
{
    public const string ManifestName = "wf-package.json";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<PackageBuildResult> BuildAsync(ScriptBuildResult build, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(build.PublishDirectory);
        var output = Path.GetFullPath(outputPath);
        if (Inside(output, root))
            throw new ArgumentException("Package output cannot be inside the published directory.", nameof(outputPath));
        var assembly = Path.GetFileName(build.AssemblyPath);
        if (!string.Equals(Path.GetFullPath(build.AssemblyPath), Path.Combine(root, assembly), PathComparison) ||
            !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || UnsafePart(assembly) ||
            string.IsNullOrWhiteSpace(build.Manifest.Version))
            throw new InvalidDataException("Published workflow entry assembly or version is invalid.");
        var files = await InventoryAsync(root, cancellationToken);
        if (!files.ContainsKey(assembly))
            throw new InvalidDataException("Published workflow entry assembly is missing.");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new PackageManifest(build.Manifest.Version, assembly),
            PackageBuilderJsonContext.Default.PackageManifest);
        if (manifest.Length > 16 * 1024)
            throw new InvalidDataException("Compiled-package manifest exceeds 16 KiB.");
        if (files.Count + 1 > 10000 || files.Values.Sum(file => file.Size) + manifest.Length > MaxExpandedBytes)
            throw new InvalidDataException("Compiled package exceeds the agent entry-count or expanded-size limit.");
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var staging = Path.Combine(directory, $".wf-package-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var name in files.Keys.Append(ManifestName).Order(StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                        entry.LastWriteTime = Epoch;
                        entry.ExternalAttributes = 0;
                        await using var destination = entry.Open();
                        if (name == ManifestName)
                            await destination.WriteAsync(manifest, cancellationToken);
                        else
                        {
                            var file = files[name];
                            RejectLinks(file.Path);
                            await using var source = File.OpenRead(file.Path);
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            var buffer = new byte[64 * 1024];
                            long copied = 0;
                            int count;
                            while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                            {
                                copied += count;
                                if (copied > file.Size)
                                    throw new IOException($"Published file changed while packaging: {name}");
                                hash.AppendData(buffer, 0, count);
                                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                            }
                            if (copied != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                                throw new IOException($"Published file changed while packaging: {name}");
                        }
                    }
                }
                if (stream.Length > MaxArchiveBytes)
                    throw new InvalidDataException("Compiled package exceeds the 512 MiB agent download limit.");
                await stream.FlushAsync(cancellationToken);
                var size = stream.Length;
                stream.Position = 0;
                var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                var after = await InventoryAsync(root, cancellationToken);
                if (files.Count != after.Count || files.Any(file =>
                    !after.TryGetValue(file.Key, out var current) || current != file.Value))
                    throw new IOException("Published inventory changed while packaging.");
                cancellationToken.ThrowIfCancellationRequested();
                // Close before rename on Windows. The sibling staging file keeps replacement on one filesystem.
                await stream.DisposeAsync();
                File.Move(staging, output, overwrite: true);
                return new(output, sha256, size);
            }
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    private static async Task<SortedDictionary<string, PublishedFile>> InventoryAsync(string root, CancellationToken token)
    {
        RejectLinks(root);
        var files = new SortedDictionary<string, PublishedFile>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        var directories = new Stack<string>();
        directories.Push(root);
        long expanded = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(item);
                var name = Path.GetRelativePath(root, item).Replace(Path.DirectorySeparatorChar, '/');
                if (!Inside(item, root) || name.Split('/').Any(UnsafePart) || !names.Add(name))
                    throw new InvalidDataException($"Published inventory contains an unsafe or duplicate path: {name}");
                if (Directory.Exists(item))
                    directories.Push(item);
                else
                {
                    await using var source = File.OpenRead(item);
                    expanded = checked(expanded + source.Length);
                    if (expanded > MaxExpandedBytes || files.Count >= 9999)
                        throw new InvalidDataException("Published inventory exceeds the agent size or entry-count limit.");
                    files.Add(name, new(item, source.Length,
                        Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token))));
                }
            }
        }
        return files;
    }

    private static bool UnsafePart(string part) =>
        part is "" or "." or ".." || part.IndexOfAny([':', '\\', '\0']) >= 0;

    private static bool Inside(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Packages cannot contain symbolic links or reparse points: {current}");
    }

    private sealed record PublishedFile(string Path, long Size, string Sha256);
}

internal sealed record PackageManifest(string Version, string EntryAssembly);

[JsonSerializable(typeof(PackageManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PackageBuilderJsonContext : JsonSerializerContext;
