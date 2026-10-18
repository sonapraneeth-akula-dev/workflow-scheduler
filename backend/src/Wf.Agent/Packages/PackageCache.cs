using System.Buffers;
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
    public const long DefaultMaxCacheBytes = 4L * 1024 * 1024 * 1024;
    public const int DefaultMaxPackages = 32;
    public const string ManifestName = "wf-package.json";
    private readonly string root;
    private readonly HttpClient http;
    private readonly ILogger<PackageCache> logger;
    private readonly SemaphoreSlim writes = new(1);
    private readonly Dictionary<string, PackageLease.Pin> pins = new(StringComparer.Ordinal);
    private readonly long maxCacheBytes;
    private readonly int maxPackages;
    private readonly TimeProvider timeProvider;
    private readonly Uri? insecureOrigin;
    private bool disposed;

    public PackageCache(string root, ILogger<PackageCache> logger, HttpMessageHandler? handler = null,
        long maxCacheBytes = DefaultMaxCacheBytes, int maxPackages = DefaultMaxPackages, TimeProvider? timeProvider = null,
        Uri? insecureOrigin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Package-cache root must be absolute.", nameof(root));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPackages);
        if (insecureOrigin is not null && (!insecureOrigin.IsAbsoluteUri ||
            insecureOrigin.Scheme != Uri.UriSchemeHttp || insecureOrigin.AbsolutePath != "/" ||
            insecureOrigin.UserInfo.Length != 0 || insecureOrigin.Query.Length != 0 || insecureOrigin.Fragment.Length != 0))
            throw new ArgumentException("An isolated-network package origin must be an explicit HTTP origin.", nameof(insecureOrigin));
        this.insecureOrigin = insecureOrigin;
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.maxCacheBytes = maxCacheBytes;
        this.maxPackages = maxPackages;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ValidateRoot();
        Directory.CreateDirectory(this.root);
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) });
    }

    /// <summary>Returns a verified, pinned package. Dispose it after use; hosts retain their own process-lifetime pin.</summary>
    public async Task<TaskHostPackage> GetAsync(string version, Uri packageUri, string sha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(packageUri);
        if (sha256 is null || sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Compiled-package SHA-256 must contain 64 hexadecimal digits.", nameof(sha256));
        if (!packageUri.IsAbsoluteUri || packageUri.Scheme is not ("https" or "http") ||
            (packageUri.Scheme == "http" && !packageUri.IsLoopback &&
                !string.Equals(packageUri.GetLeftPart(UriPartial.Authority),
                    insecureOrigin?.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) ||
            packageUri.UserInfo.Length != 0 || packageUri.Fragment.Length != 0)
            throw new ArgumentException("Packages require HTTPS, loopback HTTP or the explicitly configured isolated-network HTTP origin, without user info or fragments.", nameof(packageUri));
        sha256 = sha256.ToLowerInvariant();
        var target = Path.Combine(root, sha256);
        await writes.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ValidateRoot();
            var entries = Inventory(cancellationToken);
            if (Directory.Exists(target))
            {
                var package = await VerifyAsync(target, version, sha256, cancellationToken);
                await MakeRoomAsync(entries, sha256, 0, cancellationToken);
                return Lease(package, sha256, entries);
            }
            var staging = Path.Combine(root, $".{sha256}.{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            byte[]? copyBuffer = null;
            try
            {
                copyBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                var archivePath = Path.Combine(staging, "package.zip");
                using var response = await http.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                    throw new InvalidDataException("Compiled package exceeds the 512 MiB download limit.");
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1, FileOptions.Asynchronous))
                    await CopyBoundedAsync(source, destination, MaxArchiveBytes, copyBuffer.AsMemory(0, 64 * 1024), cancellationToken);
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
                            1, FileOptions.Asynchronous);
                        await CopyBoundedAsync(input, output, entry.Length, copyBuffer.AsMemory(0, 64 * 1024), cancellationToken);
                        if (output.Length != entry.Length) throw new InvalidDataException("Compiled-package entry length is invalid.");
                    }
                }
                var package = await ReadManifestAsync(files, version, cancellationToken);
                var size = Measure(staging, cancellationToken);
                await MakeRoomAsync(entries, sha256, size, cancellationToken);
                await PublishAsync(staging, target, cancellationToken);
                logger.LogInformation("Cached verified compiled package {Sha256}; no compilation was performed.", sha256);
                return Lease(new TaskHostPackage(package.Version, "dotnet", [Path.Combine(target, "files", package.EntryAssembly)],
                    Path.Combine(target, "files")), sha256, entries);
            }
            finally
            {
                if (copyBuffer is not null) ArrayPool<byte>.Shared.Return(copyBuffer);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        finally { writes.Release(); }
    }

    private void ValidateRoot()
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Exists) RejectLink(directory.FullName);
    }

    internal async Task<TaskHostPackage> RestoreExistingAsync(TaskHostPackage recorded, CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ValidateRoot();
            var files = new DirectoryInfo(recorded.WorkingDirectory);
            var target = files.Parent ?? throw new InvalidDataException("Recovered package has no cache directory.");
            if (files.Name != "files" || !string.Equals(target.Parent?.FullName, root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                target.Name.Length != 64 || target.Name.Any(character => !char.IsAsciiHexDigit(character)))
                throw new InvalidDataException("Recovered package is outside the configured cache.");
            var package = await VerifyAsync(target.FullName, recorded.Version, target.Name, cancellationToken);
            if (!recorded.Matches(package))
                throw new InvalidDataException("Recovered package runtime does not match its verified manifest.");
            return Lease(package, target.Name, []);
        }
        finally { writes.Release(); }
    }

    private sealed record CacheEntry(string Hash, string Directory, long Bytes, DateTime LastUse);

    private List<CacheEntry> Inventory(CancellationToken token)
    {
        var entries = new List<CacheEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (OperatingSystem.IsWindows()) name = name.ToLowerInvariant();
            if (name.Length != 64 || name.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                continue;
            entries.Add(new CacheEntry(name, directory, Measure(directory, token), Directory.GetLastWriteTimeUtc(directory)));
        }
        return entries;
    }

    private static long Measure(string directory, CancellationToken token)
    {
        RejectLink(directory);
        var archive = Path.Combine(directory, "package.zip");
        var files = Path.Combine(directory, "files");
        if (!File.Exists(archive) || !Directory.Exists(files) ||
            Directory.EnumerateFileSystemEntries(directory).Any(path => path != archive && path != files))
            throw new InvalidDataException("Compiled-package cache entry has an invalid layout.");
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            RejectLink(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                RejectLink(path);
                if (Directory.Exists(path)) pending.Push(path);
                else bytes = checked(bytes + new FileInfo(path).Length);
            }
        }
        return bytes;
    }

    private async Task MakeRoomAsync(List<CacheEntry> entries, string requestedHash, long incomingBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var requested = entries.Find(entry => entry.Hash == requestedHash);
        if ((requested?.Bytes ?? incomingBytes) > maxCacheBytes)
            throw new IOException("Compiled package exceeds the configured package-cache byte budget.");
        var bytes = entries.Aggregate(incomingBytes, (sum, entry) => checked(sum + entry.Bytes));
        var count = entries.Count + (requested is null ? 1 : 0);
        var victims = new List<CacheEntry>();
        foreach (var entry in entries.OrderBy(entry => entry.LastUse).ThenBy(entry => entry.Hash, StringComparer.Ordinal))
        {
            if (bytes <= maxCacheBytes && count <= maxPackages) break;
            if (entry.Hash == requestedHash || (pins.TryGetValue(entry.Hash, out var pin) && pin.InUse)) continue;
            victims.Add(entry);
            bytes -= entry.Bytes;
            count--;
        }
        if (bytes > maxCacheBytes || count > maxPackages)
            throw new IOException("Package-cache capacity is exhausted by pinned packages; retry after their leases are released.");
        foreach (var entry in victims)
        {
            ValidateRoot();
            _ = Measure(entry.Directory, token);
            await VerifyHashAsync(Path.Combine(entry.Directory, "package.zip"), entry.Hash, token);
        }
        foreach (var entry in victims)
        {
            token.ThrowIfCancellationRequested();
            ValidateRoot();
            _ = Measure(entry.Directory, token);
            Directory.Delete(entry.Directory, recursive: true);
            pins.Remove(entry.Hash);
            logger.LogInformation("Evicted unused compiled package {Sha256} ({Bytes} bytes).", entry.Hash, entry.Bytes);
        }
    }

    private TaskHostPackage Lease(TaskHostPackage package, string hash, List<CacheEntry> entries)
    {
        var lastUse = entries.Count == 0 ? DateTime.MinValue : entries.Max(entry => entry.LastUse);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Persist ordering on the verified directory, not in a client-editable access-time journal.
        if (lastUse >= now) now = lastUse.AddMilliseconds(1);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, hash), now);
        if (!pins.TryGetValue(hash, out var pin)) pins.Add(hash, pin = new PackageLease.Pin());
        return new TaskHostPackage(package.Version, package.Executable, package.Arguments, package.WorkingDirectory)
        {
            CacheLease = new PackageLease(pin)
        };
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

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, Memory<byte> buffer, CancellationToken token)
    {
        long written = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            written = checked(written + count);
            if (written > limit) throw new InvalidDataException("Compiled package exceeds its permitted size.");
            await output.WriteAsync(buffer[..count], token);
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Compiled-package cache cannot contain symbolic links or reparse points.");
    }

    public void Dispose()
    {
        writes.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            http.Dispose();
        }
        finally { writes.Release(); }
    }
}

[JsonSerializable(typeof(CompiledPackageManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class PackageJsonContext : JsonSerializerContext;
