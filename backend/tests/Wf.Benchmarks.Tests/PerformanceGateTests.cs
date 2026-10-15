using Wf.Benchmarks;

namespace Wf.Benchmarks.Tests;

public sealed class PerformanceGateTests
{
    private static readonly EngineBenchmarkMetrics Baseline = new("same-runner", 100_000_000, 1_000_000, 1000);

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(1.10)]
    public void ImprovementsAndInclusiveTenPercentBoundaryPass(double ratio) =>
        Assert.Empty(PerformanceGate.Compare(Baseline,
            Baseline with { RunP95Nanoseconds = Baseline.RunP95Nanoseconds * ratio,
                NodeP95Nanoseconds = Baseline.NodeP95Nanoseconds * ratio }));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherMetricAboveTenPercentFails(bool run)
    {
        var current = run ? Baseline with { RunP95Nanoseconds = 110_000_001 } :
            Baseline with { NodeP95Nanoseconds = 1_100_001 };
        Assert.Contains("more than 10%", Assert.Single(PerformanceGate.Compare(Baseline, current)));
    }

    [Fact]
    public void AbsoluteLimitsCannotBeMaskedByASlowBaseline()
    {
        Assert.Empty(PerformanceGate.Validate(Baseline with { RunP95Nanoseconds = 3_000_000_000, NodeP95Nanoseconds = 10_000_000 }));
        Assert.Equal(2, PerformanceGate.Compare(Baseline with { RunP95Nanoseconds = 4_000_000_000 },
            Baseline with { NodeP95Nanoseconds = 11_000_000 }).Length);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingNonFiniteAndNonPositiveMeasurementsFail(double invalid)
    {
        Assert.Equal(2, PerformanceGate.Validate(Baseline with { RunP95Nanoseconds = invalid, NodeP95Nanoseconds = invalid }).Length);
        Assert.NotEmpty(PerformanceGate.Compare(Baseline with { RunP95Nanoseconds = invalid }, Baseline));
    }

    [Fact]
    public void EnvironmentSchemaAndSampleCountMustMatchTheMeasurementContract()
    {
        Assert.Contains("same OS/runtime", Assert.Single(PerformanceGate.Compare(Baseline, Baseline with { EnvironmentKey = "other-runner" })));
        Assert.NotEmpty(PerformanceGate.Validate(Baseline with { EnvironmentKey = "" }));
        Assert.NotEmpty(PerformanceGate.Validate(Baseline with { SchemaVersion = 2 }));
        Assert.NotEmpty(PerformanceGate.Validate(Baseline with { NodeSamples = 0 }));
        Assert.NotEmpty(PerformanceGate.Validate(Baseline with { NodeSamples = 999 }));
    }

    [Fact]
    public async Task NodeProbeMeasuresOneThousandIndividualReadyToTerminalDurations()
    {
        var samples = await new EngineCoreBenchmarks().MeasureNodeLatenciesAsync();
        Assert.Equal(1000, samples.Length);
        Assert.All(samples, sample => Assert.True(double.IsFinite(sample) && sample >= 0));
    }
}
