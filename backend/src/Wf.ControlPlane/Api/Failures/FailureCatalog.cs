using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts.Definition;

namespace Wf.ControlPlane.Api;

public sealed record FailureRule(string Id, string Contains, string Category);
public sealed record FailureRuleFile(FailureRule[] Rules);
public sealed record FailureClassification(string Type, string Category, string? RuleId);

public sealed class FailureCatalog
{
    private readonly FailureRule[] infrastructure;
    private readonly IReadOnlyDictionary<string, FailureRule[]> teams;

    private FailureCatalog(FailureRule[] infrastructure, IReadOnlyDictionary<string, FailureRule[]> teams) =>
        (this.infrastructure, this.teams) = (infrastructure, teams);

    public static FailureCatalog Load(string root)
    {
        var infrastructure = Read(Path.Combine(root, "system-infra", "rules.json"));
        var teams = new Dictionary<string, FailureRule[]>(StringComparer.Ordinal);
        var directories = Directory.GetDirectories(Path.Combine(root, "teams"));
        if (directories.Length > 1024)
            throw new InvalidDataException("The failure catalog supports at most 1024 teams.");
        foreach (var directory in directories.Order(StringComparer.Ordinal))
        {
            var team = Path.GetFileName(directory);
            if (!CatalogQuery.ValidSegment(team))
                throw new InvalidDataException($"Invalid failure catalog team directory: {team}.");
            teams.Add(team, Read(Path.Combine(directory, "rules.json")));
        }
        return new(infrastructure, teams);
    }

    public FailureClassification Classify(string team, string? message, FailureCategory? runtimeCategory)
    {
        if (runtimeCategory is { } category && !Enum.IsDefined(category))
            throw new InvalidDataException("Unknown runtime failure category.");
        if (message is not null)
        {
            foreach (var rule in infrastructure)
                if (message.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                    return new("Infra", rule.Category, "system-infra/" + rule.Id);
            if (teams.TryGetValue(team, out var rules))
                foreach (var rule in rules)
                    if (message.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                        return new("Service", rule.Category, team + "/" + rule.Id);
        }
        var type = runtimeCategory is FailureCategory.AgentLost or FailureCategory.PlatformError or
            FailureCategory.Placement or FailureCategory.ResourceLimit ? "Infra" : "Service";
        return new(type, runtimeCategory == FailureCategory.Placement ? "Placement" : "Unclassified", null);
    }

    private static FailureRule[] Read(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException($"Failure rule file exceeds 64 KiB: {path}.");
        var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), FailureCatalogJson.Default.FailureRuleFile)
            ?? throw new InvalidDataException($"Failure rule file is null: {path}.");
        if (file.Rules is null || file.Rules.Length > 128)
            throw new InvalidDataException($"Failure rule file exceeds 128 rules: {path}.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in file.Rules)
            if (rule is null || rule.Id.Length is < 1 or > 80 ||
                rule.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.')) ||
                !ids.Add(rule.Id) || !Text(rule.Contains, 256) || !Text(rule.Category, 64))
                throw new InvalidDataException($"Failure rules require unique alphanumeric IDs and bounded, nonblank match/category text: {path}.");
        return file.Rules;

        static bool Text(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
            value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(FailureRuleFile))]
internal partial class FailureCatalogJson : JsonSerializerContext;
