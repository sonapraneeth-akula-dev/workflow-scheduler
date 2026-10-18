using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Registry;

public sealed record TeamRegistry(string Team, ImmutableArray<WorkflowTypeDefinition> Types,
    [property: JsonPropertyName("$schema")] string? Schema = null,
    ImmutableArray<RegistryRoleBinding> RoleBindings = default, TeamPresentation? Presentation = null,
    string Deployment = "Production")
{
    public ImmutableArray<RegistryRoleBinding> RoleBindings { get; init; } =
        RoleBindings.IsDefault ? [] : RoleBindings;
}

public sealed record TeamPresentation(ImmutableArray<string> TypeOrder = default,
    ImmutableArray<string> GroupingKeys = default, ImmutableArray<string> ListColumns = default,
    ImmutableDictionary<string, string> DefaultFilters = null!, ImmutableDictionary<string, string> Labels = null!)
{
    public ImmutableArray<string> TypeOrder { get; init; } = TypeOrder.IsDefault ? [] : TypeOrder;
    public ImmutableArray<string> GroupingKeys { get; init; } = GroupingKeys.IsDefault ? [] : GroupingKeys;
    public ImmutableArray<string> ListColumns { get; init; } = ListColumns.IsDefault ? [] : ListColumns;
    public ImmutableDictionary<string, string> DefaultFilters { get; init; } =
        DefaultFilters ?? ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> Labels { get; init; } =
        Labels ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record WorkflowTypeDefinition(string Team, string Name, string Description,
    MetadataSchema MetadataSchema, ImmutableArray<RegistryRoleBinding> RoleBindings = default)
{
    public ImmutableArray<RegistryRoleBinding> RoleBindings { get; init; } =
        RoleBindings.IsDefault ? [] : RoleBindings;
}

[JsonConverter(typeof(RegistryRoleJsonConverter))]
public enum RegistryRole { Viewer, Operator, Author, Admin }

public sealed class RegistryRoleJsonConverter() : JsonStringEnumConverter<RegistryRole>(allowIntegerValues: false);

public sealed record RegistryRoleBinding(RegistryRole Role, Guid GroupId);

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
