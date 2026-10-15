using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Runs;

public sealed record TriggerRunRequest(string WorkflowId, string? Version = null)
{
    public ImmutableDictionary<string, JsonElement> Variables { get; set; } =
        ImmutableDictionary<string, JsonElement>.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record TriggerRunResponse(Guid RunId = default);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TriggerRunRequest))]
[JsonSerializable(typeof(TriggerRunResponse))]
public partial class RunJsonContext : JsonSerializerContext;
