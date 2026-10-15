using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Wf.Contracts.Definition;

namespace Wf.Validation;

public static class VariableBinder
{
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
