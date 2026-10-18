using Wf.Contracts.Definition;

namespace Wf.Validation.Tests;

public sealed class PolicyRulesTests
{
    [Fact]
    public void LimitsAreInclusiveAndExactWorstCaseDoesNotWarn()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromMinutes(1),
            new(10, new(BackoffKind.Fixed, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10))), false);
        Assert.Empty(Validate(policy, TimeSpan.FromMinutes(111), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void RetryLimitNamesTaskAndLimit()
    {
        var error = Assert.Single(Validate(new(TimeSpan.FromSeconds(1), new(11), false),
            TimeSpan.FromHours(1)));
        Assert.Equal(PolicyDiagnosticSeverity.Error, error.Severity);
        Assert.Contains("10 retries", error.Message);
        AssertNamed(error);
    }

    [Fact]
    public void DelayLimitAppliesEvenWhenNoRetriesAreRequested()
    {
        var error = Assert.Single(Validate(new(TimeSpan.FromSeconds(1),
            new(0, new(BackoffKind.Fixed, TimeSpan.Zero, TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1))), false),
            TimeSpan.FromHours(1)));
        Assert.Equal(PolicyDiagnosticSeverity.Error, error.Severity);
        Assert.Contains("10 minutes", error.Message);
        AssertNamed(error);
    }

    [Fact]
    public void AttemptTimeoutAboveBothLimitsReportsBoth()
    {
        var diagnostics = Validate(new(TimeSpan.FromHours(2), new(), false),
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));
        var errors = diagnostics.Where(d => d.Severity == PolicyDiagnosticSeverity.Error).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, d => d.Message.Contains("node timeout limit 00:30:00", StringComparison.Ordinal));
        Assert.Contains(errors, d => d.Message.Contains("run timeout limit 01:00:00", StringComparison.Ordinal));
        Assert.All(diagnostics, AssertNamed);
    }

    [Theory]
    [InlineData(BackoffKind.Fixed, 14.4)]
    [InlineData(BackoffKind.Exponential, 33.6)]
    public void WorstCaseIncludesMaximumJitterAndEveryBackoff(BackoffKind kind, double seconds)
    {
        var policy = new ExecutionPolicy(TimeSpan.FromSeconds(1),
            new(3, new(kind, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(100))), false);
        var exact = TimeSpan.FromSeconds(4 + seconds);
        Assert.Empty(Validate(policy, exact));
        var warning = Assert.Single(Validate(policy, exact - TimeSpan.FromTicks(1)));
        Assert.Equal(PolicyDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("run timeout limit", warning.Message);
        AssertNamed(warning);
    }

    [Fact]
    public void WorstCaseCapsJitterAndExponentialDelay()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromSeconds(1),
            new(3, new(BackoffKind.Exponential, TimeSpan.MaxValue, TimeSpan.FromMinutes(10))), false);
        Assert.Empty(Validate(policy, TimeSpan.FromSeconds(1804)));
        Assert.Equal(PolicyDiagnosticSeverity.Warning,
            Assert.Single(Validate(policy, TimeSpan.FromSeconds(1803))).Severity);
    }

    [Fact]
    public void PlatformRetriesAreIncludedOnlyWhenEnabled()
    {
        Assert.Empty(Validate(new(TimeSpan.FromSeconds(10), new(), false), TimeSpan.FromSeconds(10)));
        var warning = Assert.Single(Validate(new(TimeSpan.FromSeconds(10), new()), TimeSpan.FromSeconds(10)));
        Assert.Equal(PolicyDiagnosticSeverity.Warning, warning.Severity);
        Assert.Empty(Validate(new(TimeSpan.FromSeconds(10), new()), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ExtremeDurationsAndCountsCannotOverflowOrLoopOverUntrustedCount()
    {
        var diagnostics = Validate(new(TimeSpan.MaxValue, new(10)), TimeSpan.MaxValue);
        Assert.Equal(PolicyDiagnosticSeverity.Warning, Assert.Single(diagnostics).Severity);
        var error = Assert.Single(Validate(new(TimeSpan.MaxValue, new(int.MaxValue)), TimeSpan.MaxValue));
        Assert.Equal(PolicyDiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void AllNodesAndStageTasksAreValidatedInOrder()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromSeconds(1), new(11), false);
        var diagnostics = PolicyRules.Validate([
            new("$post", [], [new("cleanup", policy: policy)]),
            new("node", [], [new("a", policy: policy), new("b", policy: policy)])
        ], TimeSpan.FromHours(1));
        Assert.Equal(["$post/cleanup", "node/a", "node/b"], diagnostics.Select(d => $"{d.NodeKey}/{d.TaskKey}"));
        Assert.All(diagnostics, d => Assert.Equal(PolicyDiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void InvalidRunTimeoutAndNullNodesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PolicyRules.Validate([], TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => PolicyRules.Validate(null!, TimeSpan.FromHours(1)));
    }

    private static System.Collections.Immutable.ImmutableArray<PolicyDiagnostic> Validate(
        ExecutionPolicy policy, TimeSpan runTimeout, TimeSpan? nodeTimeout = null) =>
        PolicyRules.Validate([new("node", [], [new("work", policy: policy)], timeout: nodeTimeout)], runTimeout);

    private static void AssertNamed(PolicyDiagnostic diagnostic)
    {
        Assert.Equal("node", diagnostic.NodeKey);
        Assert.Equal("work", diagnostic.TaskKey);
        Assert.Contains("node/work", diagnostic.Message);
    }
}
