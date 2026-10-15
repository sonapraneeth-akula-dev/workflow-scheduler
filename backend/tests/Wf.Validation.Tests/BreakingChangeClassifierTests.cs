using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Validation.SemVer;

namespace Wf.Validation.Tests;

public sealed class BreakingChangeClassifierTests
{
    [Fact]
    public void RemovedAndRenamedNodesAreMajorWithoutGuessingRenameIdentity()
    {
        var result = BreakingChangeClassifier.Classify(
            Baseline(nodes: ["retired", "original", "kept"]),
            Baseline("1.3.0", nodes: ["renamed", "kept"]));

        Assert.Equal(SemVerBump.Major, result.RequiredBump);
        Assert.Equal([
            new ApiChange(ApiChangeKind.NodeRemoved, "original", SemVerBump.Major),
            new ApiChange(ApiChangeKind.NodeRemoved, "retired", SemVerBump.Major),
            new ApiChange(ApiChangeKind.NodeAdded, "renamed", SemVerBump.Minor)
        ], result.Changes);
        AssertWrongBump(result, "MAJOR");
    }

    [Fact]
    public void RemovedAndRenamedVariablesAreMajorAndKeysAreOrdinal()
    {
        var result = BreakingChangeClassifier.Classify(
            Baseline(variables: [new("input", VariableType.String), new("retired", VariableType.Int)]),
            Baseline("1.3.0", variables: [new("Input", VariableType.String)]));

        Assert.Equal([
            new ApiChange(ApiChangeKind.VariableRemoved, "input", SemVerBump.Major),
            new ApiChange(ApiChangeKind.VariableRemoved, "retired", SemVerBump.Major),
            new ApiChange(ApiChangeKind.VariableAdded, "Input", SemVerBump.Minor)
        ], result.Changes);
        AssertWrongBump(result, "MAJOR");
    }

    [Theory]
    [InlineData(VariableType.String, VariableType.Int)]
    [InlineData(VariableType.Int, VariableType.Long)]
    [InlineData(VariableType.Long, VariableType.Double)]
    [InlineData(VariableType.Double, VariableType.Bool)]
    [InlineData(VariableType.Bool, VariableType.Enum)]
    [InlineData(VariableType.Enum, VariableType.StringList)]
    [InlineData(VariableType.StringList, VariableType.Duration)]
    [InlineData(VariableType.Duration, VariableType.Secret)]
    [InlineData(VariableType.Secret, VariableType.String)]
    public void EveryVariableTypeChangeIsMajor(VariableType oldType, VariableType newType)
    {
        var result = Compare(Variable(oldType), Variable(newType));

        Assert.Contains(new(ApiChangeKind.VariableTypeChanged, "input", SemVerBump.Major), result.Changes);
        AssertWrongBump(result, "MAJOR");
    }

    [Theory]
    [InlineData(false, false, SemVerBump.Minor)]
    [InlineData(false, true, SemVerBump.Minor)]
    [InlineData(true, false, SemVerBump.Major)]
    [InlineData(true, true, SemVerBump.Minor)]
    public void AddedVariableIsCompatibleOnlyWhenOmissionStillWorks(
        bool required, bool hasDefault, SemVerBump bump)
    {
        var variable = new VariableSchema("input", VariableType.String, required,
            hasDefault ? Json("\"dev\"") : null);
        var result = BreakingChangeClassifier.Classify(Baseline(),
            Baseline("1.2.4", [variable]));

        Assert.Equal(bump, result.RequiredBump);
        Assert.Equal(new(ApiChangeKind.VariableAdded, "input", bump), Assert.Single(result.Changes));
        AssertWrongBump(result, bump == SemVerBump.Major ? "MAJOR" : "MINOR");
    }

    [Fact]
    public void AddedNodesRequireAtLeastMinorAndLargerBumpsAreAllowed()
    {
        var old = Baseline(nodes: ["a"]);
        var wrong = BreakingChangeClassifier.Classify(old, Baseline("1.2.4", nodes: ["a", "b"]));

        Assert.Equal(new(ApiChangeKind.NodeAdded, "b", SemVerBump.Minor), Assert.Single(wrong.Changes));
        AssertWrongBump(wrong, "MINOR");
        Assert.True(BreakingChangeClassifier.Classify(old, Baseline("1.3.0", nodes: ["b", "a"])).IsValid);
        Assert.True(BreakingChangeClassifier.Classify(old, Baseline("2.0.0", nodes: ["b", "a"])).IsValid);
    }

    [Theory]
    [InlineData(false, true, false, SemVerBump.Major)]
    [InlineData(false, true, true, SemVerBump.Minor)]
    [InlineData(true, false, false, SemVerBump.Minor)]
    [InlineData(true, false, true, SemVerBump.Minor)]
    public void RequiredChangesFollowDefaultFirstBinding(
        bool oldRequired, bool newRequired, bool hasDefault, SemVerBump bump)
    {
        var value = hasDefault ? Json("\"dev\"") : (JsonElement?)null;
        var old = new VariableSchema("input", VariableType.String, oldRequired, value);
        var current = new VariableSchema("input", VariableType.String, newRequired, value);
        var result = Compare(old, current);

        Assert.Equal(bump, result.RequiredBump);
        Assert.Equal(new(ApiChangeKind.VariableRequiredChanged, "input", bump), Assert.Single(result.Changes));
        AssertWrongBump(result, bump == SemVerBump.Major ? "MAJOR" : "MINOR");
    }

    [Theory]
    [InlineData(null, "\"dev\"")]
    [InlineData("\"dev\"", null)]
    [InlineData("\"dev\"", "\"prod\"")]
    public void AnyDefaultChangeIsConservativelyMajor(string? oldValue, string? newValue)
    {
        var result = Compare(
            new("input", VariableType.String, @default: oldValue is null ? null : Json(oldValue)),
            new("input", VariableType.String, @default: newValue is null ? null : Json(newValue)));

        Assert.Equal(new(ApiChangeKind.VariableDefaultChanged, "input", SemVerBump.Major),
            Assert.Single(result.Changes));
        AssertWrongBump(result, "MAJOR");
    }

    [Theory]
    [InlineData(VariableType.String, "\"dev\"", "\"prod\"")]
    [InlineData(VariableType.Int, "1", "2")]
    [InlineData(VariableType.Long, "2147483648", "2147483649")]
    [InlineData(VariableType.Double, "1.5", "2.5")]
    [InlineData(VariableType.Bool, "true", "false")]
    [InlineData(VariableType.Enum, "\"dev\"", "\"prod\"")]
    [InlineData(VariableType.StringList, "[\"a\",\"b\"]", "[\"b\",\"a\"]")]
    [InlineData(VariableType.Duration, "\"00:01:00\"", "\"00:02:00\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"old-secret\"}", "{\"Name\":\"new-secret\"}")]
    public void DefaultComparisonCoversEveryTypeWithoutLeakingValues(
        VariableType type, string oldValue, string newValue)
    {
        var allowed = type == VariableType.Enum ? ImmutableArray.Create("dev", "prod") : [];
        var result = Compare(new("input", type, @default: Json(oldValue), allowedValues: allowed),
            new("input", type, @default: Json(newValue), allowedValues: allowed));

        Assert.Equal(new(ApiChangeKind.VariableDefaultChanged, "input", SemVerBump.Major),
            Assert.Single(result.Changes));
        AssertWrongBump(result, "MAJOR");
        Assert.DoesNotContain(oldValue, result.Errors[0].Message + result.Errors[0].Hint);
        Assert.DoesNotContain(newValue, result.Errors[0].Message + result.Errors[0].Hint);
    }

    [Fact]
    public void SemanticallyEqualDefaultsAndReorderedSetsArePatchAndDoNotMutateInputs()
    {
        var old = Baseline(variables: [
            new("number", VariableType.Double, @default: Json("1.0")),
            new("input", VariableType.String, allowedValues: ["b", "a"])
        ], nodes: ["z", "A", "a"]);
        var current = Baseline("1.2.4", [
            new("input", VariableType.String, allowedValues: ["a", "b"]),
            new("number", VariableType.Double, @default: Json("1e0"))
        ], ["a", "z", "A"]);
        var beforeOld = old.WriteUtf8();
        var beforeNew = current.WriteUtf8();

        var result = BreakingChangeClassifier.Classify(old, current);

        Assert.True(result.IsValid);
        Assert.Equal(SemVerBump.Patch, result.RequiredBump);
        Assert.Empty(result.Changes);
        Assert.Empty(result.Errors);
        Assert.Equal(beforeOld, old.WriteUtf8());
        Assert.Equal(beforeNew, current.WriteUtf8());
    }

    [Theory]
    [InlineData("", "a,b", SemVerBump.Major)]
    [InlineData("a,b", "a", SemVerBump.Major)]
    [InlineData("a,b", "b,c", SemVerBump.Major)]
    [InlineData("A", "a", SemVerBump.Major)]
    [InlineData("a", "a,b", SemVerBump.Minor)]
    [InlineData("a,b", "", SemVerBump.Minor)]
    public void StringAllowedValuesUseOrdinalAcceptedInputSets(
        string oldAllowed, string newAllowed, SemVerBump bump)
    {
        var result = Compare(
            new("input", VariableType.String, allowedValues: Allowed(oldAllowed)),
            new("input", VariableType.String, allowedValues: Allowed(newAllowed)));

        Assert.Equal(new(ApiChangeKind.VariableAllowedValuesChanged, "input", bump), Assert.Single(result.Changes));
        AssertWrongBump(result, bump == SemVerBump.Major ? "MAJOR" : "MINOR");
    }

    [Theory]
    [InlineData("a,b", "a", SemVerBump.Major)]
    [InlineData("a", "a,b", SemVerBump.Minor)]
    public void EnumAllowedValuesUseTheSameCompatibilityPolicy(
        string oldAllowed, string newAllowed, SemVerBump bump)
    {
        var result = Compare(
            new("input", VariableType.Enum, allowedValues: Allowed(oldAllowed)),
            new("input", VariableType.Enum, allowedValues: Allowed(newAllowed)));

        Assert.Equal(bump, result.RequiredBump);
        Assert.Equal(new(ApiChangeKind.VariableAllowedValuesChanged, "input", bump), Assert.Single(result.Changes));
    }

    [Fact]
    public void AllChangedFacetsAreReportedDeterministicallyAndMajorDominatesMinor()
    {
        var old = Baseline(variables: [
            new("z", VariableType.String, @default: Json("\"a\""), allowedValues: ["a", "b"]),
            new("A", VariableType.Int)
        ], nodes: ["removed"]);
        var current = Baseline("2.0.0", [
            new("new", VariableType.Bool),
            new("z", VariableType.String, true, allowedValues: ["a"]),
            new("A", VariableType.Long)
        ], ["added"]);

        var result = BreakingChangeClassifier.Classify(old, current);

        Assert.True(result.IsValid);
        Assert.Equal(SemVerBump.Major, result.RequiredBump);
        Assert.Equal([
            new ApiChange(ApiChangeKind.NodeRemoved, "removed", SemVerBump.Major),
            new ApiChange(ApiChangeKind.NodeAdded, "added", SemVerBump.Minor),
            new ApiChange(ApiChangeKind.VariableAdded, "new", SemVerBump.Minor),
            new ApiChange(ApiChangeKind.VariableTypeChanged, "A", SemVerBump.Major),
            new ApiChange(ApiChangeKind.VariableRequiredChanged, "z", SemVerBump.Major),
            new ApiChange(ApiChangeKind.VariableDefaultChanged, "z", SemVerBump.Major),
            new ApiChange(ApiChangeKind.VariableAllowedValuesChanged, "z", SemVerBump.Major)
        ], result.Changes);
        Assert.Equal(result.Changes, BreakingChangeClassifier.Classify(
            ApiBaseline.Parse(old.WriteUtf8()), ApiBaseline.Parse(current.WriteUtf8())).Changes);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.9", "1.2.10-alpha")]
    [InlineData("1.2.3+z", "1.2.4+a")]
    [InlineData("1.2.3-alpha", "1.2.3-alpha.1")]
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha.beta")]
    [InlineData("1.2.3-alpha.beta", "1.2.3-beta")]
    [InlineData("1.2.3-beta", "1.2.3-beta.2")]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.11")]
    [InlineData("1.2.3-beta.11", "1.2.3-rc.1")]
    [InlineData("1.2.3-rc.1", "1.2.3")]
    [InlineData("1.2.3-9", "1.2.3-10")]
    [InlineData("1.2.3-99999999999999999999999999999", "1.2.3-A")]
    [InlineData("1.2.3-A", "1.2.3-a")]
    [InlineData("1.2.3-a", "1.2.3-a-")]
    [InlineData("1.2.3-99999999999999999999999999999", "1.2.3-100000000000000000000000000000")]
    [InlineData("1.2.99999999999999999999999999999", "1.2.100000000000000000000000000000")]
    public void ForwardPatchAndPrereleaseProgressionAreAcceptedAndReverseIsRejected(
        string oldVersion, string newVersion)
    {
        var result = BreakingChangeClassifier.Classify(Baseline(oldVersion), Baseline(newVersion));

        Assert.True(result.IsValid);
        Assert.Equal(SemVerBump.Patch, result.RequiredBump);
        AssertWrongBump(BreakingChangeClassifier.Classify(Baseline(newVersion), Baseline(oldVersion)), "PATCH");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+a", "1.2.3+b")]
    [InlineData("1.2.3", "1.2.3+build.01")]
    [InlineData("1.2.3-alpha+1", "1.2.3-alpha+2")]
    public void EqualPrecedenceIncludingBuildOnlyChangesCannotSatisfyPatch(string oldVersion, string newVersion) =>
        AssertWrongBump(BreakingChangeClassifier.Classify(Baseline(oldVersion), Baseline(newVersion)), "PATCH");

    [Theory]
    [InlineData("1.2.3", "1.3.0-alpha")]
    [InlineData("1.9.9", "1.10.0")]
    [InlineData("1.99999999999999999999999999999.0", "1.100000000000000000000000000000.0")]
    [InlineData("1.2.3", "2.0.0-rc.1+build.42")]
    public void CompatibleAdditionsAcceptMinorOrLargerIncludingUnboundedNumbers(
        string oldVersion, string newVersion)
    {
        var result = BreakingChangeClassifier.Classify(Baseline(oldVersion),
            Baseline(newVersion, nodes: ["added"]));

        Assert.True(result.IsValid);
        Assert.Equal(SemVerBump.Minor, result.RequiredBump);
    }

    [Theory]
    [InlineData("1.2.3", "2.0.0-alpha")]
    [InlineData("0.2.3", "1.0.0")]
    [InlineData("99999999999999999999999999999.2.3", "100000000000000000000000000000.0.0")]
    public void BreakingChangesAcceptMajorIncludingPreleasesAndZeroMajor(string oldVersion, string newVersion)
    {
        var result = BreakingChangeClassifier.Classify(Baseline(oldVersion, nodes: ["removed"]),
            Baseline(newVersion));

        Assert.True(result.IsValid);
        Assert.Equal(SemVerBump.Major, result.RequiredBump);
    }

    [Theory]
    [InlineData("1.2.3", "1.99.99")]
    [InlineData("2.0.0-alpha", "2.0.0-beta")]
    [InlineData("2.0.0-rc.1", "2.0.0")]
    [InlineData("0.2.3", "0.3.0")]
    [InlineData("2.0.0", "1.99.99")]
    public void MajorRequiresAnIncreasedMajorNotOnlyForwardPrecedence(string oldVersion, string newVersion) =>
        AssertWrongBump(BreakingChangeClassifier.Classify(
            Baseline(oldVersion, nodes: ["removed"]), Baseline(newVersion)), "MAJOR");

    [Theory]
    [InlineData("1.3.0-alpha", "1.3.0-beta")]
    [InlineData("1.3.0-rc.1", "1.3.0")]
    [InlineData("1.3.0", "1.3.1")]
    [InlineData("2.0.0", "1.99.99")]
    public void MinorRequiresIncreasedMinorOrMajorNotOnlyForwardPrecedence(string oldVersion, string newVersion) =>
        AssertWrongBump(BreakingChangeClassifier.Classify(Baseline(oldVersion),
            Baseline(newVersion, nodes: ["added"])), "MINOR");

    [Fact]
    public void NullBaselinesAreRejectedAtThePublicBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => BreakingChangeClassifier.Classify(null!, Baseline()));
        Assert.Throws<ArgumentNullException>(() => BreakingChangeClassifier.Classify(Baseline(), null!));
    }

    private static void AssertWrongBump(BreakingChangeResult result, string required)
    {
        Assert.False(result.IsValid);
        Assert.Equal(new("WFV010", ApiBaseline.FileName, "$.version",
            "The workflow version does not satisfy the required semantic version bump.",
            $"Use a version newer than the baseline with at least a {required} bump."),
            Assert.Single(result.Errors));
        Assert.Equal("WFV010", BreakingChangeClassifier.VersionBumpRuleId);
        Assert.Equal(RuleCatalog.VersionBump.Id, BreakingChangeClassifier.VersionBumpRuleId);
    }

    private static BreakingChangeResult Compare(VariableSchema old, VariableSchema current) =>
        BreakingChangeClassifier.Classify(Baseline(variables: [old]), Baseline("1.2.4", [current]));

    private static ApiBaseline Baseline(string version = "1.2.3",
        ImmutableArray<VariableSchema> variables = default, ImmutableArray<string> nodes = default) =>
        new(version, variables.IsDefault ? [] : variables, nodes.IsDefault ? [] : nodes);

    private static VariableSchema Variable(VariableType type) =>
        new("input", type, allowedValues: type == VariableType.Enum ? ["a", "b"] : []);

    private static ImmutableArray<string> Allowed(string values) =>
        values.Length == 0 ? [] : [.. values.Split(',')];

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
