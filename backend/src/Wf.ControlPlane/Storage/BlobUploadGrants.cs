using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Wf.ControlPlane.Storage;

/// <summary>Stateless, retry-idempotent bearer grants for streamed uploads through any IBlobStore.</summary>
public sealed class BlobUploadGrants : IDisposable
{
    private readonly byte[] key;
    private readonly long maximumBytes;
    private readonly TimeProvider time;
    private bool disposed;

    public BlobUploadGrants(ReadOnlySpan<byte> hmacKey, long maximumBytes = BlobStreaming.DefaultMaximumBytes,
        TimeProvider? timeProvider = null)
    {
        if (hmacKey.Length < 32) throw new ArgumentException("Use at least 256 random key bits.", nameof(hmacKey));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        key = hmacKey.ToArray();
        this.maximumBytes = maximumBytes;
        time = timeProvider ?? TimeProvider.System;
    }

    public string Create(BlobContent content, long maxBytes, TimeSpan lifetime)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        BlobStreaming.Validate(content, maximumBytes);
        if (maxBytes < content.SizeBytes || maxBytes > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new GrantPayload(content.Kind, content.Sha256,
            content.SizeBytes, content.Key, maxBytes, BlobStreaming.Expiry(time, lifetime).UtcTicks,
            Guid.NewGuid().ToString("N")));
        return $"{WebEncoders.Base64UrlEncode(payload)}.{WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload))}";
    }

    public BlobContent Read(string grant)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            if (string.IsNullOrEmpty(grant) || grant.Length > 2048) throw new FormatException();
            var parts = grant.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payload = WebEncoders.Base64UrlDecode(parts[0]);
            var signature = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload), signature))
                throw new FormatException();
            var data = JsonSerializer.Deserialize<GrantPayload>(payload) ?? throw new FormatException();
            var content = new BlobContent(data.Kind, data.Sha256, data.SizeBytes,
                data.Kind == BlobKind.Logs && data.Key is not null && data.Key.Count(character => character == '/') == 4 ? data.Key : null);
            BlobStreaming.Validate(content, maximumBytes);
            var now = time.GetUtcNow().UtcTicks;
            if (data.Key != content.Key || data.MaxBytes < content.SizeBytes || data.MaxBytes > maximumBytes ||
                data.ExpiresUtcTicks <= now || data.ExpiresUtcTicks - now > BlobStreaming.MaximumGrantLifetime.Ticks ||
                data.Nonce is not { Length: 32 })
                throw new FormatException();
            return content;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or InvalidDataException)
        {
            throw new UnauthorizedAccessException("Invalid or expired upload grant.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }

    private sealed record GrantPayload(BlobKind Kind, string Sha256, long SizeBytes, string Key,
        long MaxBytes, long ExpiresUtcTicks, string Nonce);
}
