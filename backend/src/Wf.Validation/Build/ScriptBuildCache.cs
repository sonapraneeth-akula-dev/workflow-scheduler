using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts.Manifest;

namespace Wf.Validation.Build;

/// <summary>
/// Prototype content-addressed, framework-dependent .NET 10 file-app publish + --describe cache.
/// Executes trusted workflow code, not a sandbox. Hits verify stored bytes, never re-execute --describe.
/// Describe must be deterministic from declared inputs: ambient files, network, clock, floating NuGet
/// versions, package-feed changes and arbitrary MSBuild imports outside the input closure are NOT tracked.
/// Include local dependency trees/configuration explicitly; pin packages. File changes during a miss fail
/// rather than publish stale output (this is not an immutable filesystem snapshot).
/// Cache integrity detects accidental corruption, not malicious modification by a cache owner.
/// Entries/lock files are persistent; eviction and resource/output limits belong to the future host.
/// </summary>
public sealed class ScriptBuildCache
{
    internal const string BuildSettings =
        "Release;PublishAot=false;SelfContained=false;UseAppHost=false;PublishSingleFile=false;PublishTrimmed=false;" +
        "UseSharedCompilation=false;MSBUILDDISABLENODEREUSE=1";
    private const string InventoryFile = "integrity.json";
    private readonly string cacheDirectory;
    private readonly string artifactsDirectory;
    private readonly string dotnet;

    public ScriptBuildCache(string cacheDirectory, string artifactsDirectory, string dotnet = "dotnet")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnet);
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.artifactsDirectory = Path.GetFullPath(artifactsDirectory);
        this.dotnet = dotnet;
        if (ScriptBuildInputs.Inside(this.cacheDirectory, this.artifactsDirectory) ||
            ScriptBuildInputs.Inside(this.artifactsDirectory, this.cacheDirectory))
            throw new ArgumentException("Cache and build artifacts must be separate, non-nested directories.");
    }

    public async Task<string> ComputeKeyAsync(ScriptBuildRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sdk = await ReadSdkAsync(request, cancellationToken);
        return await HashAsync(request, sdk, cancellationToken);
    }

    public async Task<ScriptBuildResult> BuildAsync(ScriptBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timer = Stopwatch.StartNew();
        var sdk = await ReadSdkAsync(request, cancellationToken);
        var key = await HashAsync(request, sdk, cancellationToken);
        var keyDuration = timer.Elapsed;
        Directory.CreateDirectory(cacheDirectory);
        await using var keyLock = await AcquireLockAsync(key, cancellationToken);
        var entry = Path.Combine(cacheDirectory, key);
        if (Directory.Exists(entry))
            return await ReadEntryAsync(entry, key, true, keyDuration, TimeSpan.Zero, TimeSpan.Zero, cancellationToken);
        if (File.Exists(entry))
            throw new InvalidDataException($"Cache entry is not a directory: {entry}");

        var staging = Path.Combine(cacheDirectory, $".staging-{Guid.NewGuid():N}");
        var artifacts = Path.Combine(artifactsDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var publish = Path.Combine(staging, "publish");
            timer.Restart();
            await ScriptBuildProcess.RunAsync(dotnet,
                ["publish", Path.GetFullPath(request.ScriptPath), "--configuration", "Release",
                    "--output", publish, "--artifacts-path", artifacts,
                    "-p:PublishAot=false", "-p:SelfContained=false", "-p:UseAppHost=false",
                    "-p:PublishSingleFile=false", "-p:PublishTrimmed=false",
                    "-p:UseSharedCompilation=false", "--nologo"],
                Path.GetDirectoryName(Path.GetFullPath(request.ScriptPath))!, "publish", cancellationToken);
            var publishDuration = timer.Elapsed;
            var runtimeConfigs = Directory.GetFiles(publish, "*.runtimeconfig.json");
            if (runtimeConfigs.Length != 1)
                throw new InvalidDataException("Publish must produce exactly one application runtimeconfig.");
            var assemblyName = Path.GetFileName(runtimeConfigs[0])[..^".runtimeconfig.json".Length] + ".dll";
            var assembly = Path.Combine(publish, assemblyName);
            if (!File.Exists(assembly))
                throw new InvalidDataException("Publish did not produce the workflow assembly.");
            timer.Restart();
            var manifest = await ScriptBuildProcess.RunAsync(dotnet, [assembly, "--describe"],
                Path.GetDirectoryName(Path.GetFullPath(request.ScriptPath))!, "describe", cancellationToken);
            ParseManifest(manifest);
            var describeDuration = timer.Elapsed;
            if (key != await HashAsync(request, sdk, cancellationToken))
                throw new IOException("Build inputs changed during publish/describe; no cache entry was committed.");
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"), manifest, cancellationToken);
            var inventory = new ScriptBuildInventory(key, assemblyName, await FileHashesAsync(staging, cancellationToken));
            await File.WriteAllBytesAsync(Path.Combine(staging, InventoryFile),
                JsonSerializer.SerializeToUtf8Bytes(inventory, ScriptBuildCacheJsonContext.Default.ScriptBuildInventory),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, entry);
            return await ReadEntryAsync(entry, key, false, keyDuration, publishDuration, describeDuration, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            // Each invocation owns its artifacts directory; never clean another build's obj/bin.
            if (Directory.Exists(artifacts))
                Directory.Delete(artifacts, recursive: true);
        }
    }

    private async Task<string> ReadSdkAsync(ScriptBuildRequest request, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString(await ScriptBuildProcess.RunAsync(dotnet, ["--info"],
            Path.GetDirectoryName(Path.GetFullPath(request.ScriptPath))!, "sdk", cancellationToken));

    private Task<string> HashAsync(ScriptBuildRequest request, string sdk, CancellationToken cancellationToken) =>
        ScriptBuildInputs.HashAsync(request, sdk, dotnet, cacheDirectory, artifactsDirectory, cancellationToken);

    private async Task<FileStream> AcquireLockAsync(string key, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(cacheDirectory, key + ".lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static async Task<ScriptBuildResult> ReadEntryAsync(string entry, string key, bool hit,
        TimeSpan keyDuration, TimeSpan publishDuration, TimeSpan describeDuration, CancellationToken cancellationToken)
    {
        ScriptBuildInventory inventory;
        try
        {
            inventory = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(entry, InventoryFile),
                cancellationToken), ScriptBuildCacheJsonContext.Default.ScriptBuildInventory) ??
                throw new InvalidDataException("Cache inventory is null.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Cache inventory is invalid JSON.", error);
        }
        if (inventory.Key != key || string.IsNullOrWhiteSpace(inventory.AssemblyName) ||
            inventory.AssemblyName != Path.GetFileName(inventory.AssemblyName) || inventory.Files is null)
            throw new InvalidDataException("Cache inventory is invalid.");
        var actual = await FileHashesAsync(entry, cancellationToken);
        if (inventory.Files.Count != actual.Count ||
            actual.Any(file => !inventory.Files.TryGetValue(file.Key, out var expected) || expected != file.Value))
            throw new InvalidDataException($"Cache integrity check failed: {key}");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(entry, "manifest.json"), cancellationToken);
        var assembly = Path.Combine(entry, "publish", inventory.AssemblyName);
        if (!File.Exists(assembly))
            throw new InvalidDataException("Cached workflow assembly is missing.");
        return new(key, Path.Combine(entry, "publish"), assembly, bytes, ParseManifest(bytes), hit,
            keyDuration, publishDuration, describeDuration);
    }

    private static WorkflowManifest ParseManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                new[] { "sdkVersion", "name", "version", "metadata", "variables", "stages", "nodes", "runTimeout" }
                    .Any(field => !document.RootElement.TryGetProperty(field, out _)))
                throw new InvalidDataException("--describe output is missing required manifest fields.");
            Wf.Validation.Rules.ManifestRules.ThrowIfInvalid(document.RootElement);
            var manifest = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.SdkVersion) ||
                string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version) ||
                manifest.Metadata is null || manifest.Nodes.IsDefault ||
                manifest.Variables.IsDefault || manifest.Stages.IsDefault)
                throw new InvalidDataException("--describe output is missing required manifest fields.");
            return manifest;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("--describe output is not a WorkflowManifest.", error);
        }
    }

    private static async Task<Dictionary<string, string>> FileHashesAsync(string directory,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            if (relative == InventoryFile)
                continue;
            await using var stream = File.OpenRead(file);
            result.Add(relative, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        return result;
    }
}

internal sealed record ScriptBuildInventory(string Key, string AssemblyName, Dictionary<string, string> Files);

[JsonSerializable(typeof(ScriptBuildInventory))]
internal partial class ScriptBuildCacheJsonContext : JsonSerializerContext;
