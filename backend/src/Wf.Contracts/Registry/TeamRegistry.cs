using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Registry;

public sealed record TeamRegistry(string Team, ImmutableArray<WorkflowTypeDefinition> Types,
    [property: JsonPropertyName("$schema")] string? Schema = null);

public sealed record WorkflowTypeDefinition(string Team, string Name, string Description,
    MetadataSchema MetadataSchema);

public sealed record MetadataSchema(ImmutableArray<MetadataFieldSchema> Keys, bool AllowFreeForm = false);

public sealed record MetadataFieldSchema(string Key, bool Required = false,
    ImmutableArray<string> AllowedValues = default)
{
    public ImmutableArray<string> AllowedValues { get; init; } =
        AllowedValues.IsDefault ? [] : AllowedValues;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(TeamRegistry))]
public partial class TeamRegistryJsonContext : JsonSerializerContext;
