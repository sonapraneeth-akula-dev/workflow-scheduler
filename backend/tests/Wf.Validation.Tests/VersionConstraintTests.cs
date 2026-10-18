using Wf.Validation.SemVer;

namespace Wf.Validation.Tests;

public sealed class VersionConstraintTests
{
    [Theory]
    [InlineData(">=1.0.0 <2.0.0", "1.9.0")]
    [InlineData(">= 1.0.0 < 2.0.0", "1.9.0")]
    [InlineData("1.*", "1.9.0")]
    [InlineData("1.1.*", "1.1.3+build")]
    [InlineData("1.0.0 || >=2.0.0 <3.0.0", "2.1.0")]
    [InlineData("1.1.3+other", "1.1.3+build")]
    [InlineData(">=2.0.0-alpha <2.0.0", "2.0.0-beta.2")]
    [InlineData(">2.0.0-beta.1 <=2.0.0-beta.2", "2.0.0-beta.2")]
    [InlineData("3.*", null)]
    [InlineData(">2.1.0 <2.1.0", null)]
    [InlineData("<=1.1.0", "1.0.0")]
    [InlineData("*", "2.1.0")]
    public void Selects_highest_matching_precedence_and_excludes_unrequested_prereleases(string pin, string? expected)
    {
        string[] versions = ["1.0.0", "1.1.3+build", "1.9.0", "2.0.0-alpha", "2.0.0-beta.1", "2.0.0-beta.2", "2.1.0", "3.0.0-beta"];
        Assert.Equal(expected, VersionConstraint.Parse(pin).SelectHighest(versions));
        Assert.Equal(expected, VersionConstraint.Parse(pin).SelectHighest(versions.Reverse()));
    }

    [Fact]
    public void Canonical_numeric_components_have_no_int32_or_int64_limit()
    {
        var constraint = VersionConstraint.Parse(">=999999999999999999999999.0.0 <1000000000000000000000000.0.0");
        Assert.Equal("999999999999999999999999.2.0", constraint.SelectHighest(
            ["1.0.0", "999999999999999999999999.1.0", "999999999999999999999999.2.0", "1000000000000000000000000.0.0"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("^1.0.0")]
    [InlineData("~1.0.0")]
    [InlineData("01.*")]
    [InlineData("1.*.*")]
    [InlineData(">=1.*")]
    [InlineData("1.0")]
    [InlineData(">=")]
    [InlineData(">=1.0.0 ||")]
    [InlineData("1.0.0 | 2.0.0")]
    [InlineData(">=1.0.0\n<2.0.0")]
    public void Rejects_invalid_or_unsupported_range_syntax(string pin) =>
        Assert.Throws<FormatException>(() => VersionConstraint.Parse(pin));

    [Fact]
    public void Bounds_expression_length_and_does_not_silently_ignore_corrupt_versions()
    {
        Assert.Throws<FormatException>(() => VersionConstraint.Parse(new string('1', 257)));
        Assert.Throws<FormatException>(() => VersionConstraint.Parse("*").SelectHighest(["corrupt"]));
    }
}
