using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Wf.Contracts.Definition;

namespace Wf.Contracts.Manifest;

public sealed record WorkflowManifest(string SdkVersion, string Name, string Version, WorkflowMetadata Metadata,
    ImmutableArray<VariableSchema> Variables, ImmutableArray<string> Stages, ImmutableArray<NodeDefinition> Nodes,
    TimeSpan RunTimeout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlacementPolicy? Placement = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(WorkflowManifest))]
public partial class WorkflowManifestJsonContext : JsonSerializerContext;
