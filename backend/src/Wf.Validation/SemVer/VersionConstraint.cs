namespace Wf.Validation.SemVer;

/// <summary>Full SemVer comparisons joined by spaces (AND) or || (OR), plus *, major.* and major.minor.*.</summary>
public sealed class VersionConstraint
{
    private readonly Comparison[][] alternatives;

    private VersionConstraint(Comparison[][] alternatives) => this.alternatives = alternatives;

    public static VersionConstraint Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
            throw Invalid();
        var alternatives = new List<Comparison[]>();
        foreach (var alternative in text.Split("||", StringSplitOptions.TrimEntries))
        {
            var tokens = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) throw Invalid();
            var comparisons = new List<Comparison>();
            for (var index = 0; index < tokens.Length; index++)
            {
                var token = tokens[index];
                var length = token.StartsWith(">=", StringComparison.Ordinal) || token.StartsWith("<=", StringComparison.Ordinal)
                    ? 2 : token[0] is '>' or '<' or '=' ? 1 : 0;
                var operation = length == 0 ? "=" : token[..length];
                var version = token[length..];
                if (version.Length == 0 && ++index < tokens.Length) version = tokens[index];
                if (version == "*" || version.EndsWith(".*", StringComparison.Ordinal))
                {
                    var prefix = version == "*" ? [] : version[..^2].Split('.');
                    if (operation != "=" || prefix.Length > 2 || prefix.Any(part => part.Length == 0 ||
                        !part.All(char.IsAsciiDigit) || part.Length > 1 && part[0] == '0'))
                        throw Invalid();
                    comparisons.Add(new("*", new(prefix, [])));
                }
                else comparisons.Add(new(operation, ParseVersion(version)));
            }
            alternatives.Add(comparisons.ToArray());
        }
        return new(alternatives.ToArray());
    }

    public string? SelectHighest(IEnumerable<string> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        string? selected = null;
        BreakingChangeClassifier.VersionParts? highest = null;
        foreach (var version in versions)
        {
            var candidate = ParseVersion(version);
            var matches = alternatives.Any(comparisons =>
                (candidate.Prerelease.Length == 0 || comparisons.Any(comparison =>
                    comparison.Version.Prerelease.Length > 0 && comparison.Version.Core.SequenceEqual(candidate.Core))) &&
                comparisons.All(comparison => comparison.Matches(candidate)));
            if (matches && (highest is null || BreakingChangeClassifier.CompareVersions(candidate, highest.Value) > 0))
            {
                selected = version;
                highest = candidate;
            }
        }
        return selected;
    }

    private static BreakingChangeClassifier.VersionParts ParseVersion(string text)
    {
        try { _ = new ApiBaseline(text, [], []); }
        catch (ArgumentException) { throw Invalid(); }
        return BreakingChangeClassifier.VersionParts.Parse(text);
    }

    private static FormatException Invalid() => new("Use a SemVer comparison range or *, major.* or major.minor.* wildcard.");

    private sealed record Comparison(string Operation, BreakingChangeClassifier.VersionParts Version)
    {
        public bool Matches(BreakingChangeClassifier.VersionParts candidate)
        {
            if (Operation == "*") return candidate.Core.Take(Version.Core.Length).SequenceEqual(Version.Core);
            var comparison = BreakingChangeClassifier.CompareVersions(candidate, Version);
            return Operation switch
            {
                "=" => comparison == 0, ">" => comparison > 0, ">=" => comparison >= 0,
                "<" => comparison < 0, "<=" => comparison <= 0,
                _ => throw new InvalidOperationException("An invalid version comparison was constructed.")
            };
        }
    }
}
