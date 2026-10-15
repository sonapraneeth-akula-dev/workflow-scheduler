using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.Validation.Rules;

/// <summary>
/// Semantic checks on --describe JSON before contract constructors run. Contract shape, JSON syntax and
/// infrastructure failures are not validation errors. Required variables without defaults are valid;
/// missing run-time values are enforced by VariableBinder, not offline definition validation.
/// </summary>
public static class ManifestRules
{
    public static ImmutableArray<ValidationError> Validate(WorkflowManifest manifest, string file = "workflow.cs",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(manifest);
        return Validate(JsonSerializer.SerializeToElement(manifest,
            WorkflowManifestJsonContext.Default.WorkflowManifest), file, cancellationToken);
    }

    public static ImmutableArray<ValidationError> Validate(JsonElement manifest, string file = "workflow.cs",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (manifest.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A manifest must be a JSON object.");
        var errors = new List<ValidationError>();
        CheckProperties(manifest, "$", file, errors, cancellationToken);
        // Duplicate JSON properties are ambiguous; do not validate whichever value a parser happens to select.
        if (errors.Count == 0)
        {
            CheckNodes(manifest, file, errors, cancellationToken);
            CheckVariables(manifest, file, errors, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return errors.OrderBy(error => error.RuleId, StringComparer.Ordinal)
            .ThenBy(error => error.Path, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static void ThrowIfInvalid(JsonElement manifest)
    {
        var errors = Validate(manifest);
        if (!errors.IsEmpty)
            throw new ManifestValidationException(errors);
    }

    private static void CheckNodes(JsonElement manifest, string file, List<ValidationError> errors,
        CancellationToken cancellationToken)
    {
        var nodes = ImmutableArray.CreateBuilder<NodeDefinition>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var graphInvalid = false;
        var index = 0;
        foreach (var node in Array(manifest, "nodes"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"$.nodes[{index++}]";
            var key = Key(node, "key");
            if (!keys.Add(key))
            {
                errors.Add(RuleCatalog.DuplicateKey.CreateError(file, path + ".key"));
                graphInvalid = true;
            }
            var tasks = new HashSet<string>(StringComparer.Ordinal);
            var taskIndex = 0;
            foreach (var task in Array(node, "tasks"))
            {
                if (!tasks.Add(Key(task, "key")))
                    errors.Add(RuleCatalog.DuplicateKey.CreateError(file, $"{path}.tasks[{taskIndex}].key"));
                taskIndex++;
            }
            var dependencies = ImmutableArray.CreateBuilder<DependencyDefinition>();
            var dependencyKeys = new HashSet<string>(StringComparer.Ordinal);
            var dependencyIndex = 0;
            foreach (var dependency in Array(node, "dependencies"))
            {
                var dependencyKey = Key(dependency, "node");
                if (!dependencyKeys.Add(dependencyKey))
                {
                    errors.Add(RuleCatalog.DuplicateKey.CreateError(file,
                        $"{path}.dependencies[{dependencyIndex}].node"));
                    graphInvalid = true;
                }
                dependencies.Add(new(dependencyKey));
                dependencyIndex++;
            }
            nodes.Add(new(key, dependencies.ToImmutable(), []));
        }
        for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            for (var dependencyIndex = 0; dependencyIndex < nodes[nodeIndex].Dependencies.Length; dependencyIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!keys.Contains(nodes[nodeIndex].Dependencies[dependencyIndex].Node))
                {
                    errors.Add(RuleCatalog.UnknownDependency.CreateError(file,
                        $"$.nodes[{nodeIndex}].dependencies[{dependencyIndex}].node"));
                    graphInvalid = true;
                }
            }
        if (graphInvalid)
            return;
        try
        {
            DagRules.Validate(nodes.ToImmutable());
        }
        catch (ArgumentException)
        {
            // Node/dependency uniqueness and references were checked above; only a cycle can remain.
            errors.Add(RuleCatalog.DependencyCycle.CreateError(file, "$.nodes"));
        }
    }

    private static void CheckVariables(JsonElement manifest, string file, List<ValidationError> errors,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var variable in Array(manifest, "variables"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"$.variables[{index++}]";
            var key = Key(variable, "key");
            if (!keys.Add(key))
                errors.Add(RuleCatalog.DuplicateKey.CreateError(file, path + ".key"));
            if (variable.TryGetProperty("required", out var required) &&
                required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add(RuleCatalog.VariableRequired.CreateError(file, path + ".required"));
            if (!variable.TryGetProperty("type", out var typeElement) || !TryType(typeElement, out var type))
            {
                errors.Add(RuleCatalog.VariableType.CreateError(file, path + ".type"));
                continue;
            }
            VariableSchema schema;
            try
            {
                var allowed = Array(variable, "allowedValues").Select(value =>
                    value.ValueKind == JsonValueKind.String ? value.GetString()! : null!).ToImmutableArray();
                schema = new(key, type, allowedValues: allowed);
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            {
                errors.Add(RuleCatalog.VariableType.CreateError(file, path + ".allowedValues"));
                continue;
            }
            if (!variable.TryGetProperty("default", out var value) || value.ValueKind == JsonValueKind.Null)
                continue;
            try
            {
                schema.Validate(value);
            }
            catch (ArgumentException)
            {
                errors.Add(RuleCatalog.VariableType.CreateError(file, path + ".default"));
            }
        }
    }

    private static bool TryType(JsonElement element, out VariableType type)
    {
        type = default;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            type = (VariableType)number;
        else if (element.ValueKind != JsonValueKind.String ||
            !Enum.TryParse(element.GetString(), ignoreCase: true, out type))
            return false;
        return Enum.IsDefined(type);
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Manifest entries must be JSON objects.");
        if (!parent.TryGetProperty(property, out var value))
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Manifest '{property}' must be an array.");
        return value.EnumerateArray();
    }

    private static string Key(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var key) ||
            key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))
            throw new InvalidDataException($"Manifest entries require a nonempty '{property}'.");
        return key.GetString()!;
    }

    private static void CheckProperties(JsonElement element, string path, string file, List<ValidationError> errors,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = property.Name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                    && property.Name.Length > 0 && !char.IsAsciiDigit(property.Name[0])
                    ? path + "." + property.Name
                    : path + "['" + property.Name.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("'", "\\'", StringComparison.Ordinal) + "']";
                if (!names.Add(property.Name))
                    errors.Add(RuleCatalog.DuplicateKey.CreateError(file, propertyPath));
                CheckProperties(property.Value, propertyPath, file, errors, cancellationToken);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                CheckProperties(item, $"{path}[{index++}]", file, errors, cancellationToken);
        }
    }
}

internal sealed class ManifestValidationException(ImmutableArray<ValidationError> errors)
    : Exception("The workflow manifest failed validation.")
{
    internal ImmutableArray<ValidationError> Errors { get; } = errors;
}
