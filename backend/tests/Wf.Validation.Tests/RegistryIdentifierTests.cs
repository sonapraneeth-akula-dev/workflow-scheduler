using Wf.Contracts.Registry;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class RegistryIdentifierTests
{
    [Theory]
    [InlineData("team/id")]
    [InlineData("team\\id")]
    [InlineData("team\nid")]
    [InlineData("team\u007fid")]
    [InlineData("team\u0085id")]
    public void Separator_or_control_character_has_shared_sanitized_error(string name)
    {
        var teamErrors = TeamRegistryRules.Validate(new TeamRegistry(name, []),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(teamErrors, error => error.RuleId == "WFV015" && error.Path == "$.team");
        var typeErrors = TeamRegistryRules.Validate(new TeamRegistry("team",
            [new("team", name, "description", new([]))]), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(typeErrors, error => error.RuleId == "WFV015" && error.Path == "$.types[0].name");
        Assert.All(teamErrors.Concat(typeErrors), error => Assert.DoesNotContain(name, error.Message));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Codepoint_limit_bounds_compound_database_keys_without_rejecting_supplementary_unicode(int length, bool valid)
    {
        var name = string.Concat(Enumerable.Repeat("\U00010400", length));
        var registry = new TeamRegistry(name, [new(name, "release", "description", new([]))]);
        var errors = TeamRegistryRules.Validate(registry, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(valid, errors.IsEmpty);
    }
}
