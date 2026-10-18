using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Registration;

public sealed record PackageReference(string Sha256, long Size);

public sealed record RegistrationRequest(string GitCommit, string GitAuthor, string? WorkflowId = null,
    JsonElement? Manifest = null, PackageReference? Package = null, string? Source = null,
    JsonElement? Registry = null, string? ChangeNote = null, string? Schedules = null,
    string? ScheduleSourcePath = null);

public sealed record RegistrationResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? WorkflowVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RegistrySnapshot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ScheduleConfigVersion = null)
{
    [JsonIgnore]
    public bool Changed => WorkflowVersion is not null || RegistrySnapshot is not null || ScheduleConfigVersion is not null;
}

public sealed record UploadGrantRequest(string Kind, string Sha256, long Size);
public sealed record UploadGrantResponse(bool Exists,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UploadUri = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MaxBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RegistrationRequest))]
[JsonSerializable(typeof(RegistrationResponse))]
[JsonSerializable(typeof(UploadGrantRequest))]
[JsonSerializable(typeof(UploadGrantResponse))]
public partial class RegistrationJsonContext : JsonSerializerContext;
