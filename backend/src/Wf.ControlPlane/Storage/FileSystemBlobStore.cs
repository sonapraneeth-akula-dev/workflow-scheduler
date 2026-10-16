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
