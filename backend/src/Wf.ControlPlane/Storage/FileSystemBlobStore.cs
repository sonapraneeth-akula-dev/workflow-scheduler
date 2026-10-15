using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Wf.ControlPlane.Storage;

/// <summary>
/// Root must be private to the service OS identity; reparse points are rejected, but managed path checks
/// cannot defend against a hostile local administrator replacing directories between syscalls.
/// </summary>
public sealed class FileSystemBlobStore
    : IBlobStore, IDisposable
{
    private readonly string root;
    private readonly byte[] signingKey;
    private readonly long maximumBytes;
    private readonly TimeProvider time;
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
        signingKey = hmacKey.ToArray();
        this.maximumBytes = maximumBytes;
        time = timeProvider ?? TimeProvider.System;
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
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BlobStreaming.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await BlobStreaming.CopyVerifiedAsync(input, destination, content, maximumBytes, cancellationToken);
    }

    /// <summary>
    /// Grant is a bearer secret. Retry/replay is idempotent for the same immutable content until expiry;
    /// grants are not one-shot tokens and need no process-local replay cache.
    /// </summary>
    public string CreateUploadGrant(BlobContent content, long maxBytes, TimeSpan lifetime)
    {
        _ = GetPath(content);
        if (maxBytes < content.SizeBytes || maxBytes > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new GrantPayload(content.Kind, content.Sha256,
            content.SizeBytes, content.Key, maxBytes, BlobStreaming.Expiry(time, lifetime).UtcTicks,
            Guid.NewGuid().ToString("N")));
        return $"{WebEncoders.Base64UrlEncode(payload)}.{WebEncoders.Base64UrlEncode(HMACSHA256.HashData(signingKey, payload))}";
    }

    public Task UploadWithGrantAsync(string grant, Stream source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            if (string.IsNullOrEmpty(grant) || grant.Length > 2048) throw new FormatException();
            var parts = grant.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payload = WebEncoders.Base64UrlDecode(parts[0]);
            var signature = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(signingKey, payload), signature))
                throw new FormatException();
            var data = JsonSerializer.Deserialize<GrantPayload>(payload) ?? throw new FormatException();
            var content = new BlobContent(data.Kind, data.Sha256, data.SizeBytes);
            var now = time.GetUtcNow().UtcTicks;
            if (data.Key != content.Key || data.MaxBytes < content.SizeBytes || data.MaxBytes > maximumBytes ||
                data.ExpiresUtcTicks <= now || data.ExpiresUtcTicks - now > BlobStreaming.MaximumGrantLifetime.Ticks ||
                data.Nonce is not { Length: 32 })
                throw new FormatException();
            return UploadAsync(content, source, cancellationToken);
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            throw new UnauthorizedAccessException("Invalid or expired upload grant.");
        }
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
        CryptographicOperations.ZeroMemory(signingKey);
    }

    private sealed record GrantPayload(BlobKind Kind, string Sha256, long SizeBytes, string Key,
        long MaxBytes, long ExpiresUtcTicks, string Nonce);
}
