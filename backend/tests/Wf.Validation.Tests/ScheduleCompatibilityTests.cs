using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Validation.Schedules;
using Wf.Validation.SemVer;

namespace Wf.Validation.Tests;

public sealed class ScheduleCompatibilityTests
{
    private const string Schedule = """
        {"defaults":{"deployment":{"environment":"dev"}},
         "instances":[{"id":"smoke","cron":"* * * * *","timezone":"Etc/UTC"}]}
        """;

    [Fact]
    public void RemovedVariableReferencedByScheduleFailsEvenAfterValidMajorBump()
    {
        var previous = Baseline("1.0.0", [Environment()], ["deploy"]);
        var current = Baseline("2.0.0", [], ["deploy"]);
        Assert.True(BreakingChangeClassifier.Classify(previous, current).IsValid);
        var error = Assert.Single(Validate(Schedule, current));
        Assert.Equal(RuleCatalog.ScheduleVariable.CreateError("schedules.json",
            "$.defaults[\"deployment\"][\"environment\"]"), error);
    }

    [Fact]
    public void RemovedOrRenamedNodesDoNotBreakSchedule()
    {
        var previous = Baseline("1.0.0", [Environment()], ["deploy"]);
        foreach (ImmutableArray<string> nodes in new[] { ImmutableArray<string>.Empty, ["renamed"] })
        {
            var current = Baseline("2.0.0", [Environment()], nodes);
            Assert.Equal(SemVerBump.Major, BreakingChangeClassifier.Classify(previous, current).RequiredBump);
            Assert.Empty(Validate(Schedule, current));
        }
    }

    [Fact]
    public void RemovedUnreferencedVariableDoesNotBreakSchedule()
    {
        var previous = Baseline("1.0.0", [Environment(), new("unused", VariableType.Int)], ["deploy"]);
        var current = Baseline("2.0.0", [Environment()], ["deploy"]);
        Assert.True(BreakingChangeClassifier.Classify(previous, current).IsValid);
        Assert.Empty(Validate(Schedule, current));
    }

    [Fact]
    public void VariableRenameIsNotInferredFromNodeOrScheduleIdentities()
    {
        var current = Baseline("2.0.0", [new("renamed", VariableType.String)], ["environment"]);
        Assert.Equal("WFV013", Assert.Single(Validate(Schedule, current)).RuleId);
    }

    [Fact]
    public void VariableTypeChangesAndNarrowedAllowedValuesRejectExistingSchedule()
    {
        var typeChanged = Baseline("2.0.0", [new("environment", VariableType.Int)]);
        var narrowed = Baseline("2.0.0", [new("environment", VariableType.Enum, allowedValues: ["prod"])]);
        Assert.Equal("WFV013", Assert.Single(Validate(Schedule, typeChanged)).RuleId);
        Assert.Equal("WFV013", Assert.Single(Validate(Schedule, narrowed)).RuleId);
        Assert.Empty(Validate(Schedule, Baseline("2.0.0",
            [new("environment", VariableType.Enum, allowedValues: ["dev", "ppe", "prod"])])));
    }

    [Fact]
    public void NewRequiredVariableNeedsAnEffectiveValueOrWorkflowDefault()
    {
        var required = new VariableSchema("count", VariableType.Int, true);
        Assert.Equal("$.instances[0].variables[\"count\"]",
            Assert.Single(Validate(Schedule, Baseline("2.0.0", [Environment(), required]))).Path);
        var defaulted = new VariableSchema("count", VariableType.Int, true, Json("2"));
        Assert.Empty(Validate(Schedule, Baseline("2.0.0", [Environment(), defaulted])));
    }

    [Fact]
    public void OverridesAreCheckedAgainstTargetVariablesNotBaselineNodes()
    {
        const string schedule = """
            {"instances":[{"id":"smoke","interval":"00:01:00","timezone":"Etc/UTC",
                "variables":{"removed":"DO-NOT-LEAK"}}]}
            """;
        var error = Assert.Single(Validate(schedule, Baseline("2.0.0", [], ["removed"])));
        Assert.Equal("$.instances[0].variables[\"removed\"]", error.Path);
        Assert.DoesNotContain("DO-NOT-LEAK", error.Message);
    }

    [Fact]
    public void EmptyScheduleIgnoresNodeAndVersionChanges()
    {
        Assert.Empty(Validate("""{"instances":[]}""", Baseline("9.0.0", [], [])));
    }

    [Fact]
    public void TargetVersionPinResolutionIsExplicitlyTheCallersResponsibility()
    {
        const string pinned = """
            {"instances":[{"id":"old","cron":"* * * * *","timezone":"Etc/UTC","versionPin":"1.0.0",
                "variables":{"environment":"dev"}}]}
            """;
        Assert.Empty(Validate(pinned, Baseline("1.0.0", [Environment()])));
        Assert.Equal("WFV013", Assert.Single(Validate(pinned, Baseline("2.0.0", []))).RuleId);
    }

    [Fact]
    public void ScheduleDiagnosticsMatchOfflineParserExactly()
    {
        var target = Baseline("2.0.0", []);
        var utf8 = Encoding.UTF8.GetBytes(Schedule);
        var expected = ScheduleFileValidator.Validate(utf8, target.Variables, "custom.json",
            TestContext.Current.CancellationToken).Errors;
        Assert.Equal(expected, ScheduleCompatibilityRule.Validate(utf8, target, "custom.json",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CancellationAndInvalidCallerArgumentsPropagate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ScheduleCompatibilityRule.Validate("invalid"u8.ToArray(),
            Baseline("1.0.0", []), cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => ScheduleCompatibilityRule.Validate("{}"u8.ToArray(), null!,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ImmutableArray<ValidationError> Validate(string schedule, ApiBaseline target) =>
        ScheduleCompatibilityRule.Validate(Encoding.UTF8.GetBytes(schedule), target,
            cancellationToken: TestContext.Current.CancellationToken);

    private static VariableSchema Environment() => new("environment", VariableType.String);
    private static ApiBaseline Baseline(string version, ImmutableArray<VariableSchema> variables,
        ImmutableArray<string> nodes = default) => new(version, variables, nodes.IsDefault ? [] : nodes);
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
