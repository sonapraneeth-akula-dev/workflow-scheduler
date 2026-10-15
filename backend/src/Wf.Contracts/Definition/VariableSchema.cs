using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Wf.Contracts.Definition;

public enum VariableType { String, Int, Long, Double, Bool, Enum, StringList, Duration, Secret }

public sealed record VariableSchema
{
    public string Key { get; }
    public VariableType Type { get; }
    public bool Required { get; }
    public JsonElement? Default { get; }
    public ImmutableArray<string> AllowedValues { get; }

    public VariableSchema(string key, VariableType type, bool required = false, JsonElement? @default = null,
        ImmutableArray<string> allowedValues = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        Key = key;
        Type = type;
        Required = required;
        AllowedValues = allowedValues.IsDefault ? [] : allowedValues;
        if (AllowedValues.Any(v => v is null) || AllowedValues.Distinct(StringComparer.Ordinal).Count() != AllowedValues.Length)
            throw new ArgumentException($"Variable '{key}' has null or duplicate allowed values.");
        if (AllowedValues.Length > 0 && type is not (VariableType.String or VariableType.Enum))
            throw new ArgumentException($"Variable '{key}' only supports allowed values for string or enum types.");
        if (type == VariableType.Enum && AllowedValues.IsEmpty)
            throw new ArgumentException($"Enum variable '{key}' needs allowed values.");
        if (@default is { } value)
        {
            Validate(value);
            Default = value.Clone();
        }
    }

    public void Validate(JsonElement value)
    {
        var valid = Type switch
        {
            VariableType.String or VariableType.Enum => value.ValueKind == JsonValueKind.String,
            VariableType.Int => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            VariableType.Long => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            VariableType.Double => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d),
            VariableType.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            VariableType.StringList => value.ValueKind == JsonValueKind.Array &&
                value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
            VariableType.Duration => value.ValueKind == JsonValueKind.String &&
                TimeSpan.TryParseExact(value.GetString(), "c", CultureInfo.InvariantCulture, out _),
            VariableType.Secret => SecretReference.IsValidValue(value),
            _ => false
        };
        if (!valid)
            throw new ArgumentException($"Variable '{Key}' requires type {Type}; secret inputs must contain a name only.");
        if (!AllowedValues.IsEmpty && !AllowedValues.Contains(value.GetString()!, StringComparer.Ordinal))
            throw new ArgumentException($"Variable '{Key}' must match one of its allowed values.");
    }
}
