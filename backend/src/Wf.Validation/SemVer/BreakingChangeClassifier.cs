using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;

namespace Wf.Validation.SemVer;

public enum SemVerBump { Patch, Minor, Major }

public enum ApiChangeKind
{
    NodeRemoved, NodeAdded, VariableRemoved, VariableAdded, VariableTypeChanged,
    VariableRequiredChanged, VariableDefaultChanged, VariableAllowedValuesChanged
}

public sealed record ApiChange(ApiChangeKind Kind, string Key, SemVerBump RequiredBump);

public sealed class BreakingChangeResult
{
    public SemVerBump RequiredBump { get; }
    public ImmutableArray<ApiChange> Changes { get; }
    public ImmutableArray<ValidationError> Errors { get; }
    public bool IsValid => Errors.IsEmpty;

    internal BreakingChangeResult(SemVerBump requiredBump, ImmutableArray<ApiChange> changes,
        ImmutableArray<ValidationError> errors) =>
        (RequiredBump, Changes, Errors) = (requiredBump, changes, errors);
}

/// <summary>Compares public APIs only; unchanged APIs still require a new PATCH-level version.</summary>
public static class BreakingChangeClassifier
{
    public static string VersionBumpRuleId => RuleCatalog.VersionBump.Id;

    /// <summary>
    /// Returns every changed facet, ordered by kind then ordinal key. Renames are removals plus additions.
    /// Defaults are public implicit inputs: any changed default is conservatively breaking.
    /// Required inputs without defaults and narrowed allowed-value sets are breaking; widening is MINOR.
    /// Bumps are minimum levels, including at major zero. Forward same-core prerelease progression is
    /// PATCH only; build metadata never advances a version. Skipped versions need not reset lower components.
    /// </summary>
    public static BreakingChangeResult Classify(ApiBaseline previous, ApiBaseline current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var changes = new List<ApiChange>();
        CompareNodes(previous, current, changes);
        CompareVariables(previous, current, changes);
        var ordered = changes.OrderBy(change => change.Kind).ThenBy(change => change.Key, StringComparer.Ordinal)
            .ToImmutableArray();
        var required = ordered.Aggregate(SemVerBump.Patch,
            (bump, change) => change.RequiredBump > bump ? change.RequiredBump : bump);
        var oldVersion = VersionParts.Parse(previous.Version);
        var newVersion = VersionParts.Parse(current.Version);
        var actual = CompareNumber(newVersion.Core[0], oldVersion.Core[0]) > 0 ? SemVerBump.Major :
            CompareNumber(newVersion.Core[1], oldVersion.Core[1]) > 0 ? SemVerBump.Minor : SemVerBump.Patch;
        ImmutableArray<ValidationError> errors = [];
        if (CompareVersions(newVersion, oldVersion) <= 0 || actual < required)
            errors = [new(VersionBumpRuleId, ApiBaseline.FileName, "$.version",
                "The workflow version does not satisfy the required semantic version bump.",
                $"Use a version newer than the baseline with at least a {required.ToString().ToUpperInvariant()} bump.")];
        return new(required, ordered, errors);
    }

    private static void CompareNodes(ApiBaseline previous, ApiBaseline current, List<ApiChange> changes)
    {
        var oldNodes = previous.Nodes.ToHashSet(StringComparer.Ordinal);
        var newNodes = current.Nodes.ToHashSet(StringComparer.Ordinal);
        foreach (var key in oldNodes)
            if (!newNodes.Contains(key))
                changes.Add(new(ApiChangeKind.NodeRemoved, key, SemVerBump.Major));
        foreach (var key in newNodes)
            if (!oldNodes.Contains(key))
                changes.Add(new(ApiChangeKind.NodeAdded, key, SemVerBump.Minor));
    }

    private static void CompareVariables(ApiBaseline previous, ApiBaseline current, List<ApiChange> changes)
    {
        var oldVariables = previous.Variables.ToDictionary(variable => variable.Key, StringComparer.Ordinal);
        var newVariables = current.Variables.ToDictionary(variable => variable.Key, StringComparer.Ordinal);
        foreach (var old in previous.Variables)
        {
            if (!newVariables.TryGetValue(old.Key, out var next))
            {
                changes.Add(new(ApiChangeKind.VariableRemoved, old.Key, SemVerBump.Major));
                continue;
            }
            if (old.Type != next.Type)
                changes.Add(new(ApiChangeKind.VariableTypeChanged, old.Key, SemVerBump.Major));
            if (old.Required != next.Required)
                changes.Add(new(ApiChangeKind.VariableRequiredChanged, old.Key,
                    next.Required && next.Default is null ? SemVerBump.Major : SemVerBump.Minor));
            if (!SameDefault(old.Default, next.Default))
                changes.Add(new(ApiChangeKind.VariableDefaultChanged, old.Key, SemVerBump.Major));
            if (!old.AllowedValues.SequenceEqual(next.AllowedValues, StringComparer.Ordinal))
                changes.Add(new(ApiChangeKind.VariableAllowedValuesChanged, old.Key,
                    NarrowsAllowedValues(old, next) ? SemVerBump.Major : SemVerBump.Minor));
        }
        foreach (var next in current.Variables)
            if (!oldVariables.ContainsKey(next.Key))
                changes.Add(new(ApiChangeKind.VariableAdded, next.Key,
                    next.Required && next.Default is null ? SemVerBump.Major : SemVerBump.Minor));
    }

    private static bool SameDefault(JsonElement? old, JsonElement? current) =>
        old is { } oldValue && current is { } newValue
            ? JsonElement.DeepEquals(oldValue, newValue)
            : old is null && current is null;

    private static bool NarrowsAllowedValues(VariableSchema old, VariableSchema current) =>
        !current.AllowedValues.IsEmpty && (old.AllowedValues.IsEmpty ||
            !current.AllowedValues.ToHashSet(StringComparer.Ordinal).IsSupersetOf(old.AllowedValues));

    private static int CompareVersions(VersionParts current, VersionParts previous)
    {
        for (var index = 0; index < current.Core.Length; index++)
        {
            var comparison = CompareNumber(current.Core[index], previous.Core[index]);
            if (comparison != 0)
                return comparison;
        }
        if (current.Prerelease.Length == 0 || previous.Prerelease.Length == 0)
            return (current.Prerelease.Length == 0 ? 1 : 0).CompareTo(previous.Prerelease.Length == 0 ? 1 : 0);
        for (var index = 0; index < Math.Min(current.Prerelease.Length, previous.Prerelease.Length); index++)
        {
            var left = current.Prerelease[index];
            var right = previous.Prerelease[index];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            var comparison = leftNumeric && rightNumeric ? CompareNumber(left, right) :
                leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) :
                StringComparer.Ordinal.Compare(left, right);
            if (comparison != 0)
                return comparison;
        }
        return current.Prerelease.Length.CompareTo(previous.Prerelease.Length);
    }

    // ApiBaseline already validates SemVer; comparing canonical digits avoids any integer-size limit.
    private static int CompareNumber(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : StringComparer.Ordinal.Compare(left, right);

    private readonly record struct VersionParts(string[] Core, string[] Prerelease)
    {
        internal static VersionParts Parse(string version)
        {
            var release = version.Split('+', 2)[0].Split('-', 2);
            return new(release[0].Split('.'), release.Length == 1 ? [] : release[1].Split('.'));
        }
    }
}
