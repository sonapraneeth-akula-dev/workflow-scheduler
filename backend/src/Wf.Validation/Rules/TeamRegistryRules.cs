using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using Wf.Contracts.Definition;
using Wf.Contracts.Registry;

namespace Wf.Validation.Rules;

/// <summary>Validates a single team's registry, then optionally its workflow's type and metadata.</summary>
public static class TeamRegistryRules
{
    public const string FileName = "team.json";
    public const int DefaultTypeQuota = 10;

    public static readonly ValidationRule RegistryShape = new("WFV015",
        "The team registry does not match its JSON contract.",
        "Provide a team and types with matching owning team, nonempty name, description and metadataSchema; identifiers must be at most 128 characters without path separators or control characters. Use supported properties without duplicate JSON keys.");

    public static readonly ValidationRule DuplicateType = new("WFV016",
        "A type duplicates or closely resembles another type in this team.",
        "Choose distinct names after ignoring case, spaces, punctuation and a trailing plural s; normalized names must have edit distance greater than two.");

    public static readonly ValidationRule TypeQuota = new("WFV017",
        "The team exceeds its configured type quota.",
        "Remove types or ask an administrator to increase the configured type quota.");

    public static readonly ValidationRule InvalidMetadataSchema = new("WFV018",
        "A type's metadata schema contains an invalid or duplicate key or allowed value.",
        "Use unique nonempty metadata keys and unique nonempty string allowed values; omit allowedValues or use an empty array for unconstrained strings.");

    public static readonly ValidationRule UnknownType = new("WFV019",
        "The workflow type is not declared in its team's registry.",
        "Use the exact name of a type declared in this team's team.json.");

    public static readonly ValidationRule InvalidMetadata = new("WFV020",
        "Workflow metadata does not satisfy its type's metadata schema.",
        "Supply required nonempty values, use allowed values, and omit undeclared keys unless allowFreeForm is true.");

    public static ImmutableArray<ValidationError> Validate(ReadOnlyMemory<byte> utf8, string file = FileName,
        int typeQuota = DefaultTypeQuota, CancellationToken cancellationToken = default) =>
        ValidateFile(utf8, null, file, "workflow.cs", typeQuota, cancellationToken);

    public static ImmutableArray<ValidationError> ValidateWorkflow(ReadOnlyMemory<byte> utf8,
        WorkflowMetadata metadata, string file = FileName, string workflowFile = "workflow.cs",
        int typeQuota = DefaultTypeQuota, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return ValidateFile(utf8, metadata, file, workflowFile, typeQuota, cancellationToken);
    }

    public static ImmutableArray<ValidationError> Validate(TeamRegistry registry, string file = FileName,
        int typeQuota = DefaultTypeQuota, CancellationToken cancellationToken = default)
    {
        CheckArguments(file, typeQuota, cancellationToken);
        ArgumentNullException.ThrowIfNull(registry);
        var errors = new List<ValidationError>();
        CheckRegistry(registry, file, typeQuota, errors, cancellationToken);
        return Ordered(errors);
    }

    private static ImmutableArray<ValidationError> ValidateFile(ReadOnlyMemory<byte> utf8,
        WorkflowMetadata? metadata, string file, string workflowFile, int typeQuota,
        CancellationToken cancellationToken)
    {
        CheckArguments(file, typeQuota, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowFile);
        TeamRegistry? registry;
        try
        {
            registry = JsonSerializer.Deserialize(utf8.Span, TeamRegistryJsonContext.Default.TeamRegistry);
        }
        catch (JsonException)
        {
            // Parser diagnostics may contain supplied values or unsafe property names.
            return [RegistryShape.CreateError(file, "$")];
        }
        if (registry is null)
            return [RegistryShape.CreateError(file, "$")];
        var errors = new List<ValidationError>();
        CheckRegistry(registry, file, typeQuota, errors, cancellationToken);
        if (errors.Count == 0 && metadata is not null)
            CheckWorkflow(registry, metadata, workflowFile, errors, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Ordered(errors);
    }

    private static void CheckArguments(string file, int typeQuota, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(typeQuota);
    }

    private static void CheckRegistry(TeamRegistry registry, string file, int typeQuota,
        List<ValidationError> errors, CancellationToken cancellationToken)
    {
        if (!ValidIdentifier(registry.Team))
            errors.Add(RegistryShape.CreateError(file, "$.team"));
        CheckRoleBindings(registry.RoleBindings, "$.roleBindings", file, errors, cancellationToken);
        if (registry.Types.IsDefault)
        {
            errors.Add(RegistryShape.CreateError(file, "$.types"));
            return;
        }
        if (registry.Types.Length > typeQuota)
            errors.Add(TypeQuota.CreateError(file, "$.types") with
            {
                Hint = TypeQuota.Hint + $" Configured type quota: {typeQuota}."
            });
        var normalizedNames = new List<(string Name, int Index)>();
        for (var index = 0; index < registry.Types.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = registry.Types[index];
            var path = $"$.types[{index}]";
            if (type is null)
            {
                errors.Add(RegistryShape.CreateError(file, path));
                continue;
            }
            if (string.IsNullOrWhiteSpace(type.Team) ||
                !StringComparer.Ordinal.Equals(type.Team, registry.Team))
                errors.Add(RegistryShape.CreateError(file, path + ".team"));
            var normalized = TypeNameSimilarity.Normalize(type.Name);
            if (!ValidIdentifier(type.Name) || normalized.Length == 0)
                errors.Add(RegistryShape.CreateError(file, path + ".name"));
            else
            {
                foreach (var previous in normalizedNames)
                    if (TypeNameSimilarity.WithinTwoEdits(normalized, previous.Name, cancellationToken))
                    {
                        errors.Add(DuplicateType.CreateError(file, path + ".name") with
                        {
                            Hint = DuplicateType.Hint + $" Conflicting declaration: $.types[{previous.Index}].name."
                        });
                        break;
                    }
                normalizedNames.Add((normalized, index));
            }
            if (string.IsNullOrWhiteSpace(type.Description))
                errors.Add(RegistryShape.CreateError(file, path + ".description"));
            CheckRoleBindings(type.RoleBindings, path + ".roleBindings", file, errors, cancellationToken);
            if (type.MetadataSchema is null)
                errors.Add(RegistryShape.CreateError(file, path + ".metadataSchema"));
            else
                CheckSchema(type.MetadataSchema, path + ".metadataSchema", file, errors, cancellationToken);
        }
        if (errors.Count == 0)
            errors.AddRange(PresentationValidator.Validate(registry, file, cancellationToken));
    }

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.EnumerateRunes().Take(129).Count() <= 128 &&
        !value.Any(character => character is '/' or '\\' || char.IsControl(character));

    private static void CheckRoleBindings(ImmutableArray<RegistryRoleBinding> bindings, string path, string file,
        List<ValidationError> errors, CancellationToken cancellationToken)
    {
        if (bindings.IsDefault)
        {
            errors.Add(RegistryShape.CreateError(file, path));
            return;
        }
        var seen = new HashSet<RegistryRoleBinding>();
        for (var index = 0; index < bindings.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = bindings[index];
            if (binding is null || !Enum.IsDefined(binding.Role) || binding.GroupId == Guid.Empty || !seen.Add(binding))
                errors.Add(RegistryShape.CreateError(file, $"{path}[{index}]"));
        }
    }

    private static void CheckSchema(MetadataSchema schema, string path, string file,
        List<ValidationError> errors, CancellationToken cancellationToken)
    {
        if (schema.Keys.IsDefault)
        {
            errors.Add(InvalidMetadataSchema.CreateError(file, path + ".keys"));
            return;
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < schema.Keys.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = schema.Keys[index];
            var fieldPath = $"{path}.keys[{index}]";
            if (field is null)
            {
                errors.Add(InvalidMetadataSchema.CreateError(file, fieldPath));
                continue;
            }
            if (string.IsNullOrWhiteSpace(field.Key) || !keys.Add(field.Key))
                errors.Add(InvalidMetadataSchema.CreateError(file, fieldPath + ".key"));
            var values = new HashSet<string>(StringComparer.Ordinal);
            if (field.AllowedValues.IsDefault)
                errors.Add(InvalidMetadataSchema.CreateError(file, fieldPath + ".allowedValues"));
            else
                for (var valueIndex = 0; valueIndex < field.AllowedValues.Length; valueIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = field.AllowedValues[valueIndex];
                    if (string.IsNullOrWhiteSpace(value) || !values.Add(value))
                        errors.Add(InvalidMetadataSchema.CreateError(file,
                            $"{fieldPath}.allowedValues[{valueIndex}]"));
                }
        }
    }

    private static void CheckWorkflow(TeamRegistry registry, WorkflowMetadata metadata, string file,
        List<ValidationError> errors, CancellationToken cancellationToken)
    {
        var type = registry.Types.FirstOrDefault(type => StringComparer.Ordinal.Equals(type.Name, metadata.Type));
        if (type is null)
        {
            errors.Add(UnknownType.CreateError(file, "$.metadata.type"));
            return;
        }
        var schema = type.MetadataSchema;
        var keys = schema.Keys.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var field in schema.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (field.Required && (!metadata.Values.TryGetValue(field.Key, out var value) ||
                string.IsNullOrWhiteSpace(value)))
                errors.Add(InvalidMetadata.CreateError(file, Property("$.metadata.values", field.Key)));
        }
        foreach (var (key, value) in metadata.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keys.TryGetValue(key, out var field)
                ? field.AllowedValues.Length > 0 && !field.AllowedValues.Contains(value, StringComparer.Ordinal) &&
                    !(field.Required && string.IsNullOrWhiteSpace(value))
                : !schema.AllowFreeForm)
                errors.Add(InvalidMetadata.CreateError(file, Property("$.metadata.values", key)));
        }
    }

    private static string Property(string parent, string key) => $"{parent}[\"{JsonEncodedText.Encode(key)}\"]";

    private static ImmutableArray<ValidationError> Ordered(List<ValidationError> errors) =>
        errors.OrderBy(error => error.File, StringComparer.Ordinal).ThenBy(error => error.RuleId, StringComparer.Ordinal)
            .ThenBy(error => error.Path, StringComparer.Ordinal).ToImmutableArray();
}
