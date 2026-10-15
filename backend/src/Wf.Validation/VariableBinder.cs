using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Wf.Contracts.Definition;

namespace Wf.Validation;

public sealed class VariableResolutionResult
{
    public bool IsValid => Errors.IsEmpty;
    public ImmutableArray<ValidationError> Errors { get; }
    public ImmutableDictionary<string, JsonElement> Values { get; }

    internal VariableResolutionResult(ImmutableArray<ValidationError> errors,
        ImmutableDictionary<string, JsonElement> values) =>
        (Errors, Values) = (errors, errors.IsEmpty ? values : ImmutableDictionary<string, JsonElement>.Empty);
}

public static class VariableBinder
{
    public static VariableResolutionResult Resolve(IEnumerable<VariableSchema> schemas,
        IReadOnlyDictionary<string, JsonElement> values, string file = "request", string path = "$.variables",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var definitions = schemas.ToImmutableDictionary(schema => schema.Key, StringComparer.Ordinal);
        var resolved = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        var errors = new List<ValidationError>();
        foreach (var key in values.Keys.Where(key => !definitions.ContainsKey(key)))
            Reject(key);
        foreach (var schema in definitions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!values.TryGetValue(schema.Key, out var value))
            {
                if (schema.Default is { } defaultValue)
                    value = defaultValue;
                else
                {
                    if (schema.Required) Reject(schema.Key);
                    continue;
                }
            }
            try
            {
                if (ContainsNul(value))
                    throw new ArgumentException("Variable values cannot contain NUL characters.");
                schema.Validate(value);
                resolved.Add(schema.Key, value.Clone());
            }
            catch (ArgumentException) { Reject(schema.Key); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(errors.OrderBy(error => error.Path, StringComparer.Ordinal).ToImmutableArray(), resolved.ToImmutable());

        void Reject(string key) =>
            errors.Add(RuleCatalog.TriggerVariable.CreateError(file, $"{path}[\"{JsonEncodedText.Encode(key)}\"]"));
    }

    public static ImmutableDictionary<string, object> Bind(IEnumerable<VariableSchema> schemas,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(values);
        var definitions = new Dictionary<string, VariableSchema>(StringComparer.Ordinal);
        foreach (var schema in schemas)
            if (!definitions.TryAdd(schema.Key, schema))
                throw new ArgumentException($"Duplicate variable key '{schema.Key}'.", nameof(schemas));
        foreach (var key in values.Keys)
            if (!definitions.ContainsKey(key))
                throw new ArgumentException($"Unknown variable '{key}'.", nameof(values));

        var bound = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var schema in definitions.Values)
        {
            if (!values.TryGetValue(schema.Key, out var value))
            {
                if (schema.Default is { } defaultValue)
                    value = defaultValue;
                else if (schema.Required)
                    throw new ArgumentException($"Required variable '{schema.Key}' is missing.", nameof(values));
                else
                    continue;
            }
            schema.Validate(value);
            bound.Add(schema.Key, Convert(schema.Type, value));
        }
        return bound.ToImmutable();
    }

    internal static bool ContainsNul(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains('\0'),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsNul),
        JsonValueKind.Object => value.EnumerateObject().Any(property =>
            property.Name.Contains('\0') || ContainsNul(property.Value)),
        _ => false
    };

    private static object Convert(VariableType type, JsonElement value) => type switch
    {
        VariableType.String or VariableType.Enum => value.GetString()!,
        VariableType.Int => value.GetInt32(),
        VariableType.Long => value.GetInt64(),
        VariableType.Double => value.GetDouble(),
        VariableType.Bool => value.GetBoolean(),
        VariableType.StringList => value.EnumerateArray().Select(v => v.GetString()!).ToImmutableArray(),
        VariableType.Duration => TimeSpan.ParseExact(value.GetString()!, "c", CultureInfo.InvariantCulture),
        VariableType.Secret => new SecretReference(value.GetProperty("Name").GetString()!),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
