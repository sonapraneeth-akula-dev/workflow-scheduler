namespace Wf.ControlPlane.Storage;

/// <summary>
/// Root must be private to the service OS identity; reparse points are rejected, but managed path checks
/// cannot defend against a hostile local administrator replacing directories between syscalls.
/// </summary>
public sealed class FileSystemBlobStore
    : IBlobStore, IDisposable
{
    private readonly string root;
    private readonly BlobUploadGrants grants;
    private readonly long maximumBytes;
    private bool disposed;

    public FileSystemBlobStore(string rootDirectory, ReadOnlySpan<byte> hmacKey,
        long maximumBytes = BlobStreaming.DefaultMaximumBytes, TimeProvider? timeProvider = null)
    {
        if (hmacKey.Length < 32) throw new ArgumentException("Use at least 256 random key bits.", nameof(hmacKey));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        root = Path.GetFullPath(rootDirectory);
        RejectLinks(root);
        Directory.CreateDirectory(root);
        RejectLinks(root);
        grants = new(hmacKey, maximumBytes, timeProvider);
        this.maximumBytes = maximumBytes;
    }

    public async Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default)
    {
        var target = GetPath(content);
        var staging = Path.Combine(root, $".upload-{Guid.NewGuid():N}");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BlobStreaming.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await BlobStreaming.CopyVerifiedAsync(source, output, content, maximumBytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(root);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            RejectLinks(target);
            try { File.Move(staging, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                await DownloadAsync(content, Stream.Null, cancellationToken);
            }
        }
        finally { File.Delete(staging); }
    }

    public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
    {
        var path = GetPath(content);
        // Duplicate no-overwrite publishers may open rename handles while we verify immutable bytes.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            BlobStreaming.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await BlobStreaming.CopyVerifiedAsync(input, destination, content, maximumBytes, cancellationToken);
    }

    public async IAsyncEnumerable<Guid> ResourceRunsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var prefix = Path.Combine(root, "resources");
        RejectLinks(prefix);
        if (!Directory.Exists(prefix)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(prefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(directory);
            var name = Path.GetFileName(directory);
            if (Guid.TryParseExact(name, "D", out var run) && run.Version == 7 && name == run.ToString("D"))
                yield return run;
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<Guid> RunObjectsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var seen = new HashSet<Guid>();
        foreach (var kind in BlobRetention.RunPrefixes)
        {
            var prefix = Path.Combine(root, kind);
            RejectLinks(prefix);
            if (!Directory.Exists(prefix)) continue;
            foreach (var directory in Directory.EnumerateDirectories(prefix))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLinks(directory);
                if (BlobRetention.IsRun(Path.GetFileName(directory), out var run) && seen.Add(run))
                    yield return run;
            }
        }
        await Task.CompletedTask;
    }

    public Task DeleteRunObjectsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        BlobRetention.ValidateRun(runId);
        foreach (var kind in BlobRetention.RunPrefixes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteTree(Path.Combine(root, kind, runId.ToString("D")), cancellationToken);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ArtifactHashesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var prefix = Path.Combine(root, "artifacts");
        RejectLinks(prefix);
        if (!Directory.Exists(prefix)) yield break;
        foreach (var file in Directory.EnumerateFiles(prefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(file);
            var name = Path.GetFileName(file);
            if (name.Length == 64 && name.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
                yield return name;
        }
        await Task.CompletedTask;
    }

    public Task DeleteArtifactAsync(string sha256, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        BlobRetention.ValidateArtifact(sha256);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(root, "artifacts", sha256);
        RejectLinks(path);
        File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeleteResourceRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        BlobRetention.ValidateRun(runId);
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = Path.Combine(root, "resources", runId.ToString("D"));
        DeleteTree(prefix, cancellationToken);
        return Task.CompletedTask;
    }

    private static void DeleteTree(string prefix, CancellationToken token)
    {
        RejectLinks(prefix);
        if (Directory.Exists(prefix))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(prefix, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(child);
            }
            token.ThrowIfCancellationRequested();
            Directory.Delete(prefix, recursive: true);
        }
    }

    public Task<Stream> OpenLogReadAsync(string key, long maximumBytes, CancellationToken cancellationToken = default)
    {
        BlobStreaming.ValidateLogRead(key, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(new BlobContent(BlobKind.Logs, new string('0', 64), 0, key));
        Stream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            BlobStreaming.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length <= Math.Min(maximumBytes, this.maximumBytes)) return Task.FromResult(input);
        input.Dispose();
        throw new InvalidDataException("Named log object exceeds its byte limit.");
    }

    /// <summary>
    /// Grant is a bearer secret. Retry/replay is idempotent for the same immutable content until expiry;
    /// grants are not one-shot tokens and need no process-local replay cache.
    /// </summary>
    public string CreateUploadGrant(BlobContent content, long maxBytes, TimeSpan lifetime)
    {
        _ = GetPath(content);
        return grants.Create(content, maxBytes, lifetime);
    }

    public Task UploadWithGrantAsync(string grant, Stream source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return UploadAsync(grants.Read(grant), source, cancellationToken);
    }

    private string GetPath(BlobContent content)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        BlobStreaming.Validate(content, maximumBytes);
        var path = Path.Combine(root, content.Key.Replace('/', Path.DirectorySeparatorChar));
        RejectLinks(path);
        return path;
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Blob storage paths cannot contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        grants.Dispose();
    }
}
