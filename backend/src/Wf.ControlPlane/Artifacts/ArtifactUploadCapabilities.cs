using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Artifacts;
using Wf.Engine.Core.Results;

namespace Wf.ControlPlane.Artifacts;

public sealed record ArtifactActor(Guid RunId, string SourceKey, short Attempt, Guid AgentId, Guid SessionId,
    long ReservationEpoch, long ExpiresUtcTicks);

public sealed class ArtifactUploadCapabilities(byte[] hmacKey, TimeProvider time) : IDisposable
{
    private readonly byte[] key = hmacKey is { Length: >= 32 } ? hmacKey.ToArray() :
        throw new ArgumentException("Artifact capability key needs at least 256 bits.", nameof(hmacKey));
    private readonly TimeProvider clock = time ?? throw new ArgumentNullException(nameof(time));
    private bool disposed;

    public UploadGrant Create(Dispatch dispatch, Guid agentId, Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var identity = ResultDeduplicator.Identity(dispatch.Task);
        var expires = dispatch.DeadlineAt.ToDateTimeOffset();
        if (identity.RunId.Version != 7 || expires <= clock.GetUtcNow() || agentId == Guid.Empty ||
            sessionId == Guid.Empty || dispatch.ReservationEpoch == 0)
            throw new ArgumentException("Artifact capability requires a live dispatched attempt.");
        var actor = new ArtifactActor(identity.RunId, identity.TaskKey, identity.Attempt, agentId, sessionId,
            checked((long)dispatch.ReservationEpoch), expires.UtcTicks);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ArtifactCapability("artifacts", actor),
            ArtifactCapabilityJson.Default.ArtifactCapability);
        return new()
        {
            Kind = "artifacts", UploadUri = ArtifactMetadata.GrantPath, MaxBytes = ArtifactMetadata.RunBudget,
            ExpiresAt = Timestamp.FromDateTimeOffset(expires),
            BearerToken = WebEncoders.Base64UrlEncode(payload) + "." +
                WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload))
        };
    }

    public ArtifactActor Read(string token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            if (token.Length is 0 or > 4096) throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payload = WebEncoders.Base64UrlDecode(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload),
                    WebEncoders.Base64UrlDecode(parts[1]))) throw new FormatException();
            var data = JsonSerializer.Deserialize(payload, ArtifactCapabilityJson.Default.ArtifactCapability)
                ?? throw new FormatException();
            if (data.Kind != "artifacts" || data.Actor is not { } actor) throw new FormatException();
            if (actor.RunId.Version != 7 || actor.Attempt <= 0 || actor.AgentId == Guid.Empty ||
                actor.SessionId == Guid.Empty || actor.ReservationEpoch <= 0 ||
                actor.SourceKey?.Split('/') is not { Length: 2 } ||
                actor.ExpiresUtcTicks <= clock.GetUtcNow().UtcTicks) throw new FormatException();
            return actor;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            throw new UnauthorizedAccessException("Invalid or expired artifact capability.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }
}

internal sealed record ArtifactCapability(string Kind, ArtifactActor Actor);

[JsonSerializable(typeof(ArtifactCapability))]
internal partial class ArtifactCapabilityJson : JsonSerializerContext;
