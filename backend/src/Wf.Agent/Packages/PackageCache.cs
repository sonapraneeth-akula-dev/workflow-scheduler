using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wf.Agent.Hosts;

namespace Wf.Agent.Packages;

public sealed record CompiledPackageManifest(string Version, string EntryAssembly);

public sealed class PackageCache : IDisposable
{
    public const long MaxArchiveBytes = 512L * 1024 * 1024;
    public const long MaxExpandedBytes = 1024L * 1024 * 1024;
    public const string ManifestName = "wf-package.json";
    private readonly string root;
    private readonly HttpClient http;
    private readonly ILogger<PackageCache> logger;
    private readonly SemaphoreSlim writes = new(1);

    public PackageCache(string root, ILogger<PackageCache> logger, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Package-cache root must be absolute.", nameof(root));
        this.root = root;
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) });
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Directory.CreateDirectory(root);
    }

    public async Task<TaskHostPackage> GetAsync(string version, Uri packageUri, string sha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(packageUri);
        if (sha256 is null || sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Compiled-package SHA-256 must contain 64 hexadecimal digits.", nameof(sha256));
        if (!packageUri.IsAbsoluteUri || packageUri.Scheme is not ("https" or "http") ||
            (packageUri.Scheme == "http" && !packageUri.IsLoopback) ||
            packageUri.UserInfo.Length != 0 || packageUri.Fragment.Length != 0)
            throw new ArgumentException("Packages require an HTTPS URL (HTTP only on loopback), without user info or fragments.", nameof(packageUri));
        sha256 = sha256.ToLowerInvariant();
        var target = Path.Combine(root, sha256);
        await writes.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(target))
                return await VerifyAsync(target, version, sha256, cancellationToken);
            var staging = Path.Combine(root, $".{sha256}.{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                var archivePath = Path.Combine(staging, "package.zip");
                using var response = await http.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                    throw new InvalidDataException("Compiled package exceeds the 512 MiB download limit.");
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous))
                    await CopyBoundedAsync(source, destination, MaxArchiveBytes, cancellationToken);
                await VerifyHashAsync(archivePath, sha256, cancellationToken);
                var files = Path.Combine(staging, "files");
                Directory.CreateDirectory(files);
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    var names = ValidateEntries(archive);
                    foreach (var (entry, relative) in names)
                    {
                        var destination = Path.Combine(files, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using var input = entry.Open();
                        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            64 * 1024, FileOptions.Asynchronous);
                        await CopyBoundedAsync(input, output, entry.Length, cancellationToken);
                        if (output.Length != entry.Length) throw new InvalidDataException("Compiled-package entry length is invalid.");
                    }
                }
                var package = await ReadManifestAsync(files, version, cancellationToken);
                await PublishAsync(staging, target, cancellationToken);
                logger.LogInformation("Cached verified compiled package {Sha256}; no compilation was performed.", sha256);
                return new TaskHostPackage(package.Version, "dotnet", [Path.Combine(target, "files", package.EntryAssembly)], Path.Combine(target, "files"));
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        finally { writes.Release(); }
    }

    private static async Task<TaskHostPackage> VerifyAsync(string directory, string version, string sha256, CancellationToken token)
    {
        RejectLink(directory);
        var archivePath = Path.Combine(directory, "package.zip");
        var files = Path.Combine(directory, "files");
        RejectLink(archivePath);
        await VerifyHashAsync(archivePath, sha256, token);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = ValidateEntries(archive);
        var expectedPaths = entries.Select(entry => Path.GetFullPath(Path.Combine(files, entry.Relative)))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var directories = new Stack<string>();
        directories.Push(files);
        while (directories.TryPop(out var current))
        {
            RejectLink(current);
            foreach (var item in Directory.EnumerateFileSystemEntries(current))
            {
                RejectLink(item);
                if (Directory.Exists(item)) directories.Push(item);
                else if (!expectedPaths.Contains(Path.GetFullPath(item)))
                    throw new InvalidDataException("Compiled-package cache contains a file not present in its verified archive.");
            }
        }
        foreach (var (entry, relative) in entries)
        {
            var path = Path.Combine(files, relative);
            for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= files.Length; parent = Path.GetDirectoryName(parent))
                RejectLink(parent);
            RejectLink(path);
            await using var expected = entry.Open();
            await using var actual = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            var expectedHash = await SHA256.HashDataAsync(expected, token);
            var actualHash = await SHA256.HashDataAsync(actual, token);
            if (actual.Length != entry.Length || !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new InvalidDataException($"Cached compiled-package file '{relative}' is corrupt.");
        }
        var manifest = await ReadManifestAsync(files, version, token);
        return new TaskHostPackage(manifest.Version, "dotnet", [Path.Combine(files, manifest.EntryAssembly)], files);
    }

    private static async Task<CompiledPackageManifest> ReadManifestAsync(string files, string version, CancellationToken token)
    {
        var path = Path.Combine(files, ManifestName);
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Compiled-package manifest exceeds 16 KiB.");
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync(stream, PackageJsonContext.Default.CompiledPackageManifest, token)
            ?? throw new InvalidDataException("Compiled-package manifest is missing.");
        if (manifest.Version != version || string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            Path.GetFileName(manifest.EntryAssembly) != manifest.EntryAssembly ||
            manifest.EntryAssembly.Contains(':') || manifest.EntryAssembly.Contains('\\') ||
            !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(files, manifest.EntryAssembly)))
            throw new InvalidDataException("Compiled-package version or entry assembly is invalid.");
        return manifest;
    }

    private static List<(ZipArchiveEntry Entry, string Relative)> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > 10000) throw new InvalidDataException("Compiled package has an invalid entry count.");
        var files = new List<(ZipArchiveEntry, string)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(part => part is "" or "." or ".." || part.Contains(':') || part.Contains('\0')) ||
                name.StartsWith('/') || !names.Add(name.TrimEnd('/')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Compiled package contains an unsafe or duplicate path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("Expanded compiled package exceeds 1 GiB.");
            if (!name.EndsWith('/')) files.Add((entry, Path.Combine(parts)));
        }
        if (!files.Any(file => file.Item2 == ManifestName))
            throw new InvalidDataException($"Compiled package requires {ManifestName}.");
        return files;
    }

    private static async Task VerifyHashAsync(string path, string sha256, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length > MaxArchiveBytes ||
            !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, token), Convert.FromHexString(sha256)))
            throw new InvalidDataException("Compiled-package SHA-256 does not match dispatch.");
    }

    private async Task PublishAsync(string staging, string target, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { Directory.Move(staging, target); return; }
            catch (IOException exception) when (OperatingSystem.IsWindows() &&
                (exception.HResult & 0xffff) is 5 or 32 or 33 && attempt < 6)
            {
                // Windows scanners can temporarily open freshly extracted assemblies without delete sharing.
                logger.LogWarning("Compiled-package publication is blocked by a Windows file lock; retry {Retry}.", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), token);
            }
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        long written = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            written = checked(written + count);
            if (written > limit) throw new InvalidDataException("Compiled package exceeds its permitted size.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Compiled-package cache cannot contain symbolic links or reparse points.");
    }

    public void Dispose()
    {
        http.Dispose();
        writes.Dispose();
    }
}

[JsonSerializable(typeof(CompiledPackageManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class PackageJsonContext : JsonSerializerContext;
