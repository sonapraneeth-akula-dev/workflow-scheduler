using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;

namespace Wf.Sdk.Local;

public static class LocalVariables
{
    public static ImmutableDictionary<string, JsonElement> Parse(IEnumerable<VariableSchema> schemas, string[] args)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(args);
        var definitions = schemas.ToDictionary(v => v.Key, StringComparer.Ordinal);
        var values = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--var" || ++index >= args.Length)
                throw new ArgumentException("Local execution accepts --var key=value arguments only.", nameof(args));
            var separator = args[index].IndexOf('=');
            if (separator <= 0)
                throw new ArgumentException("Each --var argument must use key=value.", nameof(args));
            var key = args[index][..separator];
            if (!definitions.TryGetValue(key, out var schema))
                throw new ArgumentException($"Unknown variable '{key}'.", nameof(args));
            if (values.ContainsKey(key))
                throw new ArgumentException($"Variable '{key}' was supplied more than once.", nameof(args));
            var text = args[index][(separator + 1)..];
            try
            {
                if (schema.Type is VariableType.String or VariableType.Enum or VariableType.Duration)
                    values.Add(key, JsonSerializer.SerializeToElement(text, OutputJsonContext.Default.String));
                else if (schema.Type == VariableType.Secret)
                    values.Add(key, JsonSerializer.SerializeToElement(new SecretReference(text), OutputJsonContext.Default.SecretReference));
                else
                {
                    using var document = JsonDocument.Parse(text);
                    values.Add(key, document.RootElement.Clone());
                }
            }
            catch (JsonException)
            {
                throw new ArgumentException($"Variable '{key}' requires a valid JSON value for {schema.Type}.", nameof(args));
            }
        }
        return values.ToImmutable();
    }
}
