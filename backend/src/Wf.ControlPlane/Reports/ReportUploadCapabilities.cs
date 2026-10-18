using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Reports;
using Wf.Engine.Core.Results;

namespace Wf.ControlPlane.Reports;

public sealed class ReportUploadCapabilities : IDisposable
{
    private readonly byte[] key;
    private readonly TimeProvider time;
    private bool disposed;

    public ReportUploadCapabilities(byte[] hmacKey, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);
        ArgumentNullException.ThrowIfNull(time);
        if (hmacKey.Length < 32)
            throw new ArgumentException("Report capability key needs at least 256 bits.", nameof(hmacKey));
        key = hmacKey.ToArray();
        this.time = time;
    }

    public UploadGrant Create(AttemptRef attempt, DateTimeOffset deadline)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var identity = ResultDeduplicator.Identity(attempt);
        var expires = deadline.AddDays(1);
        if (identity.RunId.Version != 7 || expires <= time.GetUtcNow())
            throw new ArgumentException("A report capability requires a version-7 run and future expiry.", nameof(attempt));
        var data = new ReportCapability("reports", identity.RunId, identity.TaskKey, identity.Attempt, expires.UtcTicks);
        var payload = JsonSerializer.SerializeToUtf8Bytes(data, ReportCapabilityJson.Default.ReportCapability);
        var token = WebEncoders.Base64UrlEncode(payload) + "." +
            WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload));
        return new UploadGrant
        {
            Kind = "reports", UploadUri = ReportUploadMetadata.EndpointPath, BearerToken = token,
            MaxBytes = 5_000_000_000, ExpiresAt = Timestamp.FromDateTimeOffset(expires)
        };
    }

    internal ResultIdentity Read(string token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > 4096)
                throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 2)
                throw new FormatException();
            var payload = WebEncoders.Base64UrlDecode(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload),
                    WebEncoders.Base64UrlDecode(parts[1])))
                throw new FormatException();
            var data = JsonSerializer.Deserialize(payload, ReportCapabilityJson.Default.ReportCapability)
                ?? throw new FormatException();
            var source = data.SourceKey?.Split('/');
            if (data.Kind != "reports" || data.RunId.Version != 7 || source is not { Length: 2 } ||
                data.ExpiresUtcTicks <= time.GetUtcNow().UtcTicks)
                throw new FormatException();
            return ResultDeduplicator.Identity(new AttemptRef
            {
                RunId = data.RunId.ToString("D"), NodeKey = source[0], TaskKey = source[1],
                Attempt = checked((uint)data.Attempt)
            });
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or OverflowException)
        {
            throw new UnauthorizedAccessException("Invalid or expired report capability.");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }
}

internal sealed record ReportCapability(string Kind, Guid RunId, string SourceKey, short Attempt, long ExpiresUtcTicks);

[JsonSerializable(typeof(ReportCapability))]
internal partial class ReportCapabilityJson : JsonSerializerContext;
