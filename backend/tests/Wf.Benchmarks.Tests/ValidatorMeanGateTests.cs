using Wf.Benchmarks;

namespace Wf.Benchmarks.Tests;

public sealed class ValidatorMeanGateTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(1_999_999_999, true)]
    [InlineData(2_000_000_000, false)]
    [InlineData(2_000_000_001, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void ValidatorMeanUsesStrictTwoSecondFinitePositiveBoundary(double mean, bool expected) =>
        Assert.Equal(expected, PerformanceGate.ValidatorMeanWithinLimit(mean));
}
