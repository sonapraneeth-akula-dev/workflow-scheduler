using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Schedules;

namespace Wf.Validation.Schedules;

public sealed record ResolvedScheduleInstance(ScheduleInstance Definition, ScheduleExpression Expression,
    ImmutableDictionary<string, JsonElement> Variables);

public sealed class ScheduleFileValidationResult
{
    public bool IsValid => Errors.IsEmpty;
    public ImmutableArray<ValidationError> Errors { get; }
    public ImmutableArray<ResolvedScheduleInstance> Instances { get; }

    internal ScheduleFileValidationResult(ImmutableArray<ValidationError> errors,
        ImmutableArray<ResolvedScheduleInstance> instances) =>
        (Errors, Instances) = (errors, errors.IsEmpty ? instances : []);
}

/// <summary>Resolves workflow defaults, categorised file defaults, then instance overrides.</summary>
public static class ScheduleFileValidator
{
    public const string FileName = "schedules.json";

    public static ScheduleFileValidationResult Validate(ReadOnlyMemory<byte> utf8,
        IEnumerable<VariableSchema> variables, string file = FileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var schemas = variables.ToImmutableDictionary(schema => schema.Key, StringComparer.Ordinal);
        var errors = new List<ValidationError>();
        using var document = Parse(utf8, file, errors);
        if (document is null)
            return new(errors.ToImmutableArray(), []);
        ScheduleFile? definition;
        try
        {
            definition = document.RootElement.Deserialize(ScheduleFileJsonContext.Default.ScheduleFile);
        }
        catch (JsonException exception)
        {
            return new([RuleCatalog.ScheduleShape.CreateError(file, exception.Path ?? "$")], []);
        }
        if (definition is null)
            return new([RuleCatalog.ScheduleShape.CreateError(file, "$")], []);

        var defaults = schemas.Values.Where(schema => schema.Default is not null)
            .ToDictionary(schema => schema.Key, schema => schema.Default!.Value, StringComparer.Ordinal);
        var seenDefaults = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (category, values) in definition.Defaults.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Property("$.defaults", category);
            if (string.IsNullOrWhiteSpace(category) || values is null)
            {
                errors.Add(RuleCatalog.ScheduleShape.CreateError(file, path));
                continue;
            }
            foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var valuePath = Property(path, key);
                if (!seenDefaults.Add(key))
                    errors.Add(RuleCatalog.ScheduleVariable.CreateError(file, valuePath));
                if (ValidValue(key, value, valuePath, schemas, file, errors))
                    defaults[key] = value.Clone();
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var resolved = ImmutableArray.CreateBuilder<ResolvedScheduleInstance>();
        for (var index = 0; index < definition.Instances.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = definition.Instances[index];
            var path = $"$.instances[{index}]";
            if (instance is null)
            {
                errors.Add(RuleCatalog.ScheduleShape.CreateError(file, path));
                continue;
            }
            if (string.IsNullOrWhiteSpace(instance.Id) || instance.Id.Any(char.IsControl) || !ids.Add(instance.Id))
                errors.Add(RuleCatalog.ScheduleIdentity.CreateError(file, path + ".id"));
            var raw = document.RootElement.GetProperty("instances")[index];
            foreach (var optional in new[] { "cron", "interval", "versionPin" })
                if (raw.TryGetProperty(optional, out var property) && property.ValueKind == JsonValueKind.Null)
                    errors.Add(RuleCatalog.ScheduleShape.CreateError(file, path + "." + optional));
            if (instance.VersionPin is { } pin && (string.IsNullOrWhiteSpace(pin) || pin.Any(char.IsControl)))
                errors.Add(RuleCatalog.ScheduleShape.CreateError(file, path + ".versionPin"));
            ScheduleExpression? expression = null;
            try
            {
                if (instance.Interval is { } interval &&
                    (interval.Ticks % TimeSpan.TicksPerSecond != 0 || interval.TotalSeconds > int.MaxValue))
                    throw new ArgumentException("Intervals must fit the whole-second storage contract.");
                expression = ScheduleExpression.Create(instance.Cron, instance.Interval, instance.Timezone);
            }
            catch (ArgumentException)
            {
                errors.Add(RuleCatalog.ScheduleExpression.CreateError(file, path));
            }
            var effective = defaults.ToImmutableDictionary(StringComparer.Ordinal).ToBuilder();
            foreach (var (key, value) in instance.Variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (ValidValue(key, value, Property(path + ".variables", key), schemas, file, errors))
                    effective[key] = value.Clone();
            foreach (var schema in schemas.Values.OrderBy(schema => schema.Key, StringComparer.Ordinal))
                if (schema.Required && !effective.ContainsKey(schema.Key))
                    errors.Add(RuleCatalog.ScheduleVariable.CreateError(file,
                        Property(path + ".variables", schema.Key)));
            if (expression is not null)
                resolved.Add(new(instance, expression, effective.ToImmutable()));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(errors.OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.RuleId, StringComparer.Ordinal).ToImmutableArray(), resolved.ToImmutable());
    }

    private static JsonDocument? Parse(ReadOnlyMemory<byte> utf8, string file, List<ValidationError> errors)
    {
        try
        {
            return JsonDocument.Parse(utf8);
        }
        catch (JsonException exception)
        {
            errors.Add(RuleCatalog.ScheduleShape.CreateError(file, exception.Path ?? "$"));
            return null;
        }
    }

    private static bool ValidValue(string key, JsonElement value, string path,
        ImmutableDictionary<string, VariableSchema> schemas, string file, List<ValidationError> errors)
    {
        if (schemas.TryGetValue(key, out var schema))
        {
            try
            {
                if (VariableBinder.ContainsNul(value))
                    throw new ArgumentException("Schedule values cannot contain NUL characters.");
                schema.Validate(value);
                return true;
            }
            catch (ArgumentException)
            {
                // Validation messages name contracts, never include supplied variable values.
            }
        }
        errors.Add(RuleCatalog.ScheduleVariable.CreateError(file, path));
        return false;
    }

    private static string Property(string parent, string key) => $"{parent}[\"{JsonEncodedText.Encode(key)}\"]";
}
