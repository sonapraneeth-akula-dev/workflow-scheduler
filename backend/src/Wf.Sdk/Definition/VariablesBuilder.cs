using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class VariablesBuilder
{
    private readonly List<VariableSchema> variables = [];

    public VariablesBuilder String(string key, string? defaultValue = null, bool required = false,
        IEnumerable<string>? allowed = null) => Add(key, VariableType.String, defaultValue, required, allowed);
    public VariablesBuilder Int(string key, int? defaultValue = null, bool required = false) =>
        Add(key, VariableType.Int, defaultValue, required);
    public VariablesBuilder Long(string key, long? defaultValue = null, bool required = false) =>
        Add(key, VariableType.Long, defaultValue, required);
    public VariablesBuilder Double(string key, double? defaultValue = null, bool required = false)
    {
        if (defaultValue is { } value && !double.IsFinite(value))
            throw new ArgumentException($"Variable '{key}' needs a finite default.", nameof(defaultValue));
        return Add(key, VariableType.Double, defaultValue, required);
    }
    public VariablesBuilder Bool(string key, bool? defaultValue = null, bool required = false) =>
        Add(key, VariableType.Bool, defaultValue, required);
    public VariablesBuilder Enum<T>(string key, T defaultValue, bool required = false,
        IEnumerable<T>? allowed = null) where T : struct, Enum => Enum<T>(key, (T?)defaultValue, required, allowed);
    public VariablesBuilder Enum<T>(string key, T? defaultValue = null, bool required = false,
        IEnumerable<T>? allowed = null) where T : struct, Enum
    {
        var values = (allowed ?? System.Enum.GetValues<T>()).ToArray();
        if (values.Length == 0 || values.Any(v => !System.Enum.IsDefined(v)) ||
            (defaultValue is { } value && !System.Enum.IsDefined(value)))
            throw new ArgumentException($"Enum variable '{key}' contains undefined or no allowed values.");
        return Add(key, VariableType.Enum, defaultValue?.ToString(), required, values.Select(v => v.ToString()));
    }
    public VariablesBuilder StringList(string key, IEnumerable<string>? defaultValue = null, bool required = false) =>
        Add(key, VariableType.StringList, defaultValue?.ToImmutableArray(), required);
    public VariablesBuilder Duration(string key, TimeSpan? defaultValue = null, bool required = false) =>
        Add(key, VariableType.Duration, defaultValue, required);
    public VariablesBuilder Secret(string key, string? secretName = null, bool required = false) =>
        Add(key, VariableType.Secret, secretName is null ? null : new SecretReference(secretName), required);

    private VariablesBuilder Add<T>(string key, VariableType type, T? defaultValue, bool required,
        IEnumerable<string>? allowed = null)
    {
        var schema = new VariableSchema(key, type, required,
            defaultValue is null ? null : JsonSerializer.SerializeToElement(defaultValue, OutputJsonContext.Default.Object),
            allowed?.ToImmutableArray() ?? []);
        if (variables.Any(v => v.Key == key))
            throw new ArgumentException($"Duplicate variable key '{key}'.", nameof(key));
        variables.Add(schema);
        return this;
    }

    internal ImmutableArray<VariableSchema> Build() => variables.ToImmutableArray();
}
