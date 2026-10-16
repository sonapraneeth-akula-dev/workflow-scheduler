namespace Wf.Benchmarks.Tests;

public sealed class RunQueryBenchmarkTests
{
    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(0.001, true)]
    [InlineData(299.999, true)]
    [InlineData(300, false)]
    [InlineData(300.001, false)]
    public void RunQuery_gate_requires_finite_positive_p95_strictly_below_300_ms(double value, bool expected) =>
        Assert.Equal(expected, PerformanceGate.RunQueryWithinLimit(value));

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RunQuery_actual_100k_seed_and_signed_http_cases_satisfy_the_complete_per_scenario_gate()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        var report = await RunQueryBenchmarks.MeasureAsync(timeout.Token);
        Assert.True(report.Passed, string.Join(", ", report.Cases.Select(result => result.Name + "=" + result.P95Ms)));
        Assert.Equal(100_000, report.SeededRows);
        Assert.Equal(25_000, report.VisibleRows);
        Assert.Equal(4, report.TeamCount);
        Assert.Equal(20, report.TypeCount);
        Assert.Equal(200, report.WorkflowCount);
        Assert.Equal(200, report.SeriesCount);
        Assert.Equal(10, report.Cases.Length);
        Assert.Equal(10, report.Cases.Select(result => result.Name).Distinct().Count());
        Assert.All(report.Durability.Values, value => Assert.Equal("on", value));
        Assert.All(report.Cases, result =>
        {
            Assert.Equal(100, result.Samples);
            Assert.Equal(10, result.Warmups);
            Assert.InRange(result.PageRows, 1, 100);
            Assert.True(result.Passed);
            Assert.True(PerformanceGate.RunQueryWithinLimit(result.P95Ms));
            Assert.True(result.P50Ms <= result.P95Ms && result.P95Ms <= result.P99Ms && result.P99Ms <= result.MaxMs);
        });
    }
}
