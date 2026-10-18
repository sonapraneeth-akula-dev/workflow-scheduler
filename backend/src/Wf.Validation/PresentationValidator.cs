using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Registry;
using Wf.Validation.Rules;

namespace Wf.Validation;

public static class PresentationValidator
{
    public static readonly ValidationRule InvalidPresentation = new("WFV024",
        "Team presentation contains an unknown, repeated or invalid type, metadata key, filter value or label.",
        "Use declared type names and metadata keys with nonempty labels; filters must satisfy a declaring type's allowed values.");

    public static ImmutableArray<ValidationError> Validate(TeamRegistry registry, string file = TeamRegistryRules.FileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        cancellationToken.ThrowIfCancellationRequested();
        var presentation = registry.Presentation;
        if (presentation is null) return [];
        if (registry.Types.IsDefault || registry.Types.Any(type => type is null || type.MetadataSchema is null ||
                type.MetadataSchema.Keys.IsDefault || type.MetadataSchema.Keys.Any(field => field is null || field.AllowedValues.IsDefault)))
            return [InvalidPresentation.CreateError(file, "$.presentation")];
        var errors = new List<ValidationError>();
        var typeNames = registry.Types.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var fields = registry.Types.SelectMany(type => type.MetadataSchema.Keys).ToArray();
        var keys = fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        CheckList(presentation.TypeOrder, "$.presentation.typeOrder", typeNames);
        CheckList(presentation.GroupingKeys, "$.presentation.groupingKeys", keys);
        CheckList(presentation.ListColumns, "$.presentation.listColumns", keys);
        if (presentation.DefaultFilters is null)
            errors.Add(InvalidPresentation.CreateError(file, "$.presentation.defaultFilters"));
        else
            foreach (var (key, value) in presentation.DefaultFilters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!keys.Contains(key) || string.IsNullOrWhiteSpace(value) ||
                    !fields.Any(field => field.Key == key && (field.AllowedValues.Length == 0 ||
                        field.AllowedValues.Contains(value, StringComparer.Ordinal))))
                    errors.Add(InvalidPresentation.CreateError(file, Property("defaultFilters", key)));
            }
        if (presentation.Labels is null)
            errors.Add(InvalidPresentation.CreateError(file, "$.presentation.labels"));
        else
            foreach (var (key, value) in presentation.Labels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!keys.Contains(key) || string.IsNullOrWhiteSpace(value))
                    errors.Add(InvalidPresentation.CreateError(file, Property("labels", key)));
            }
        return errors.OrderBy(error => error.Path, StringComparer.Ordinal).ToImmutableArray();

        void CheckList(ImmutableArray<string> values, string path, HashSet<string> allowed)
        {
            if (values.IsDefault)
            {
                errors.Add(InvalidPresentation.CreateError(file, path));
                return;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(values[index]) || !allowed.Contains(values[index]) || !seen.Add(values[index]))
                    errors.Add(InvalidPresentation.CreateError(file, $"{path}[{index}]"));
            }
        }
    }

    private static string Property(string name, string key) => $"$.presentation.{name}[\"{JsonEncodedText.Encode(key)}\"]";
}
