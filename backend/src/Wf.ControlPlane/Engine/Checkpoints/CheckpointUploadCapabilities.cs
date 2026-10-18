using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.ControlPlane.Engine.Agents;

namespace Wf.ControlPlane.Engine.Checkpoints;

public sealed class CheckpointUploadCapabilities(byte[] hmacKey, TimeProvider clock) : IDisposable
{
    private readonly byte[] key = hmacKey.Length >= 32 ? hmacKey.ToArray() :
        throw new ArgumentException("Checkpoint capability key needs at least 256 bits.", nameof(hmacKey));
    private bool disposed;

    public UploadGrant Create(Wf.Contracts.Agent.V1.Dispatch dispatch, AgentSession session)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var expires = dispatch.DeadlineAt.ToDateTimeOffset().AddDays(1);
        if (expires <= clock.GetUtcNow()) throw new ArgumentException("Checkpoint capability requires a future expiry.");
        var data = new CheckpointCapability("checkpoints", dispatch.Task.ToByteArray(), dispatch.ReservationEpoch,
            session.AgentId, session.SessionId, expires.UtcTicks);
        var payload = JsonSerializer.SerializeToUtf8Bytes(data, CheckpointCapabilityJson.Default.CheckpointCapability);
        return new()
        {
            Kind = "checkpoints", UploadUri = CheckpointProtocol.EndpointPath, MaxBytes = CheckpointProtocol.MaximumBytes,
            ExpiresAt = Timestamp.FromDateTimeOffset(expires),
            BearerToken = WebEncoders.Base64UrlEncode(payload) + "." +
                WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload))
        };
    }

    internal AgentSession Read(string token, CheckpointSaved saved)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > 8192) throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payload = WebEncoders.Base64UrlDecode(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload),
                    WebEncoders.Base64UrlDecode(parts[1])))
                throw new FormatException();
            var data = JsonSerializer.Deserialize(payload, CheckpointCapabilityJson.Default.CheckpointCapability)
                ?? throw new FormatException();
            if (data.Kind != "checkpoints" || data.ExpiresUtcTicks <= clock.GetUtcNow().UtcTicks ||
                data.AgentId == Guid.Empty || data.SessionId == Guid.Empty || data.Epoch != saved.ReservationEpoch ||
                !AttemptRef.Parser.ParseFrom(data.Attempt).Equals(saved.Task))
                throw new FormatException();
            return new(data.AgentId, data.SessionId);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or JsonException or InvalidProtocolBufferException)
        { throw new UnauthorizedAccessException("Invalid or expired checkpoint capability."); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }
}

internal sealed record CheckpointCapability(string Kind, byte[] Attempt, ulong Epoch,
    Guid AgentId, Guid SessionId, long ExpiresUtcTicks);

[JsonSerializable(typeof(CheckpointCapability))]
internal partial class CheckpointCapabilityJson : JsonSerializerContext;
