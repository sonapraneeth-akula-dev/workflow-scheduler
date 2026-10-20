using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Runs;

public sealed record TriggerRunRequest(string WorkflowId, string? Version = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunPlacement? Placement { get; init; }
    public ImmutableDictionary<string, JsonElement> Variables { get; set; } =
        ImmutableDictionary<string, JsonElement>.Empty;
}

public sealed record RunPlacement(bool SingleMachine, string? Machine = null);
public sealed record PlacementMachine(Guid Id, string Name);
public sealed record PlacementOptionsResponse(ImmutableArray<PlacementMachine> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record TriggerRunResponse(Guid RunId = default);

public sealed record RerunRunRequest(ImmutableArray<string>? FromNodes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunPlacement? Placement = null);
public sealed record RerunFingerprint(Guid ParentRunId, ImmutableArray<string>? FromNodes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunPlacement? Placement = null);

public sealed record CloneRunRequest(ImmutableDictionary<string, JsonElement> Variables, RunPlacement? Placement = null);
public sealed record CloneRunSourceResponse(Guid RunId, string WorkflowId, Guid VersionId,
    ImmutableDictionary<string, JsonElement> Variables, RunPlacement? Placement = null);
public sealed record CloneRunFingerprint(Guid ParentRunId, CloneRunRequest Request);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TriggerRunRequest))]
[JsonSerializable(typeof(TriggerRunResponse))]
[JsonSerializable(typeof(RerunRunRequest))]
[JsonSerializable(typeof(RerunFingerprint))]
[JsonSerializable(typeof(CloneRunRequest))]
[JsonSerializable(typeof(CloneRunSourceResponse))]
[JsonSerializable(typeof(CloneRunFingerprint))]
[JsonSerializable(typeof(RunPlacement))]
[JsonSerializable(typeof(PlacementOptionsResponse))]
public partial class RunJsonContext : JsonSerializerContext;
