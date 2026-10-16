using System.Buffers;
using System.Security.Cryptography;

namespace Wf.ControlPlane.Storage;

public enum BlobKind { Packages, Sources, Logs, Resources, Checkpoints, Reports, Artifacts, Perf, Parity }

/// <summary>Trusted expected content. Keys are generated, never supplied as filesystem paths.</summary>
public sealed class BlobContent
{
    public BlobContent(BlobKind kind, string sha256, long sizeBytes, string? logKey = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a 64-character SHA-256 hex digest.", nameof(sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        Kind = kind;
        Sha256 = sha256.ToLowerInvariant();
        SizeBytes = sizeBytes;
        if (logKey is not null && (kind != BlobKind.Logs || !IsLogKey(logKey)))
            throw new ArgumentException("Named log content requires a canonical attempt prefix and object name.", nameof(logKey));
        Key = logKey ?? $"{Kind.ToString().ToLowerInvariant()}/{Sha256}{(Kind == BlobKind.Packages ? ".zip" : "")}";
    }

    public BlobKind Kind { get; }
    public string Sha256 { get; }
    public long SizeBytes { get; }
    public string Key { get; }

    private static bool IsLogKey(string key)
    {
        var parts = key.Split('/');
        return parts.Length == 5 && parts[0] == "logs" &&
            Guid.TryParseExact(parts[1], "D", out var run) && run != Guid.Empty &&
            parts[2].Length == 64 && parts[2].All(char.IsAsciiHexDigit) &&
            uint.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var attempt) &&
            attempt is > 0 and <= 32767 && parts[3] == attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            (parts[4] == "index.json" || parts[4].Length is >= 13 and <= 17 && parts[4].EndsWith(".log.gz", StringComparison.Ordinal) &&
                parts[4][..^7].All(char.IsAsciiDigit));
    }
}

public interface IBlobStore
{
    /// <summary>Publishes verified content atomically; identical concurrent writes are idempotent.</summary>
    Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Success means all bytes were verified. Destination can contain untrusted partial bytes on failure;
    /// callers must discard partial output, or abort an HTTP response already started.
    /// Caller owns both streams.
    /// </summary>
    Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default);
}

internal static class BlobStreaming
{
    internal const int BufferSize = 128 * 1024;
    internal const long DefaultMaximumBytes = 5_000_000_000;
    internal static readonly TimeSpan MaximumGrantLifetime = TimeSpan.FromMinutes(15);

    internal static void Validate(BlobContent content, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (content.SizeBytes > maximumBytes) throw new InvalidDataException("Blob exceeds the configured byte limit.");
    }

    internal static DateTimeOffset Expiry(TimeProvider time, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumGrantLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Grants must expire within 15 minutes.");
        return time.GetUtcNow().Add(lifetime);
    }

    internal static async Task CopyVerifiedAsync(Stream source, Stream destination, BlobContent content,
        long maximumBytes, CancellationToken cancellationToken)
    {
        Validate(content, maximumBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long count = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Read one extra byte to reject excess input without draining an unbounded stream.
                var wanted = (int)Math.Min(BufferSize - 1, content.SizeBytes - count) + 1;
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
                if (read == 0) break;
                count += read;
                if (count > content.SizeBytes || count > maximumBytes)
                    throw new InvalidDataException("Blob exceeds the declared byte count.");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (count != content.SizeBytes ||
                !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(content.Sha256)))
                throw new InvalidDataException("Blob length or SHA-256 mismatch.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
