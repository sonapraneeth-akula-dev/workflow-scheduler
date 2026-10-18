using Wf.Benchmarks;

namespace Wf.Benchmarks.Tests;

public sealed class DispatchLatencyGateTests
{
    private static double[] Dispatch() => Enumerable.Repeat(50d, DispatchLatencyBenchmarks.DispatchSamples).ToArray();
    private static double[] Trigger() => Enumerable.Repeat(1000d, DispatchLatencyBenchmarks.TriggerSamples).ToArray();

    [Fact]
    public void Exact_counts_and_inclusive_boundaries_pass()
    {
        var dispatch = Dispatch();
        Array.Fill(dispatch, 200d, 9500, 500);
        Array.Fill(dispatch, 201d, 9900, 100);
        Assert.True(DispatchLatencyBenchmarks.Passes(dispatch, Trigger()));
    }

    [Theory]
    [InlineData(9500, 50.001)]
    [InlineData(9900, 200.001)]
    public void Either_dispatch_percentile_above_limit_fails(int rank, double value)
    {
        var dispatch = Dispatch();
        Array.Fill(dispatch, value, rank - 1, dispatch.Length - rank + 1);
        Assert.False(DispatchLatencyBenchmarks.Passes(dispatch, Trigger()));
    }

    [Fact]
    public void Trigger_percentile_above_one_second_fails()
    {
        var trigger = Trigger();
        Array.Fill(trigger, 1000.001, 94, 6);
        Assert.False(DispatchLatencyBenchmarks.Passes(Dispatch(), trigger));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_samples_fail_even_outside_percentile_ranks(double invalid)
    {
        var dispatch = Dispatch();
        dispatch[^1] = invalid;
        Assert.False(DispatchLatencyBenchmarks.Passes(dispatch, Trigger()));
        var trigger = Trigger();
        trigger[^1] = invalid;
        Assert.False(DispatchLatencyBenchmarks.Passes(Dispatch(), trigger));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(9999, 100)]
    [InlineData(10001, 100)]
    [InlineData(10000, 0)]
    [InlineData(10000, 99)]
    [InlineData(10000, 101)]
    public void Missing_extra_or_reduced_samples_fail(int dispatch, int triggers) =>
        Assert.False(DispatchLatencyBenchmarks.Passes(Enumerable.Repeat(1d, dispatch).ToArray(), Enumerable.Repeat(1d, triggers).ToArray()));

    [Fact]
    public void Gate_sorts_without_mutating_original_sample_order()
    {
        var dispatch = Dispatch();
        dispatch[0] = 200;
        var before = dispatch.ToArray();
        Assert.True(DispatchLatencyBenchmarks.Passes(dispatch, Trigger()));
        Assert.Equal(before, dispatch);
    }
}
