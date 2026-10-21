using System.Text.Json.Serialization;

namespace Wf.Contracts.Runs;

public enum SignOffStatus { NotSignedOff, SignedOff, Revoked }
public enum SignOffAction { SignOff, Revoke }

public sealed record SignOffRequest(string Reason);
public sealed record SignOffEvent(string Revision, SignOffAction Action, string Reason,
    Guid ActorId, string ActorName, DateTimeOffset At);
public sealed record SignOffState(string Revision, SignOffStatus Status, SignOffEvent? Latest);
public sealed record SignOffResponse(Guid RunId, Guid? ReportId, string WorkflowId, Guid VersionId,
    bool IsTerminal, SignOffState State, SignOffEvent[] History, string? NextBefore);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SignOffRequest))]
[JsonSerializable(typeof(SignOffResponse))]
public partial class SignOffJsonContext : JsonSerializerContext;
