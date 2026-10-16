using System.Reflection;
using Xunit.Sdk;

namespace Wf.Validation.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class RuleCoverageAttribute(string ruleId, bool valid) : Attribute
{
    public string RuleId { get; } = ruleId;
    public bool Valid { get; } = valid;
}

public sealed class RuleCatalogCoverageTests
{
    [Fact]
    public void Every_shared_rule_has_unskipped_executable_offline_positive_and_negative_tests()
    {
        foreach (var method in CoveredMethods())
        {
            var fact = method.GetCustomAttribute<FactAttribute>();
            Assert.NotNull(fact);
            Assert.True(string.IsNullOrEmpty(fact.Skip), $"{method.DeclaringType?.Name}.{method.Name} is skipped.");
            if (fact is TheoryAttribute)
                Assert.NotEmpty(method.GetCustomAttributes<InlineDataAttribute>());
        }
        AssertComplete(RuleCatalog.All, Coverage());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Removing_a_rule_or_either_direction_fails_closed(bool? keepDirection)
    {
        var incomplete = Coverage().Where(item => item.RuleId != "WFV005" ||
            keepDirection is { } valid && item.Valid == valid);
        Assert.ThrowsAny<XunitException>(() => AssertComplete(RuleCatalog.All, incomplete));
    }

    [Fact]
    public void New_rules_and_orphaned_test_markers_fail_closed()
    {
        var added = RuleCatalog.All.Add(new("WFV999", "New shared rule.", "Add both offline scenarios."));
        Assert.ThrowsAny<XunitException>(() => AssertComplete(added, Coverage()));
        Assert.ThrowsAny<XunitException>(() => AssertComplete(RuleCatalog.All,
            Coverage().Append(("WFV999", true))));
    }

    private static IEnumerable<MethodInfo> CoveredMethods() =>
        typeof(RuleCatalogCoverageTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(RuleCoverageAttribute)));

    private static IEnumerable<(string RuleId, bool Valid)> Coverage() =>
        CoveredMethods().SelectMany(method => method.GetCustomAttributes<RuleCoverageAttribute>()
            .Select(marker => (marker.RuleId, marker.Valid)));

    private static void AssertComplete(IEnumerable<ValidationRule> rules,
        IEnumerable<(string RuleId, bool Valid)> coverage)
    {
        var ruleIds = rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        var actual = coverage.ToHashSet();
        Assert.All(actual, item => Assert.Contains(item.RuleId, ruleIds));
        Assert.Empty(ruleIds.SelectMany(id => new[] { (id, true), (id, false) }).Except(actual));
    }
}

[Collection("Validator")]
public sealed class RuleCatalogCoverageIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Coverage_marked_build_rules_execute_actual_sdk_publish_and_describe_paths()
    {
        var tests = new ValidatorIntegrationTests(output);
        await tests.RealSdkWorkflowCanBeValidatedFromXunitViaTestingFacadeAndCached();
        await tests.CompilerFailureReturnsStableErrorAndPreservesRawDiagnostics();
        await tests.ActualSdkDefinitionFailureDuringDescribeReturnsCatalogueError();
    }
}
