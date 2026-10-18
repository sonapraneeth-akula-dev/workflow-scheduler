using Wf.Benchmarks;

namespace Wf.Benchmarks.Tests;

public sealed class LiveFanoutTests
{
    private static LiveFanoutReport Valid() => new(500, 100, 10_000, 50_000, 50_000, 250_000, 25_000_000,
        5, 100, new(250_000, 1, 1000, 2000, 3000), new(25_000_000, 1, 1000, 2000, 3000),
        new() { ["fsync"] = "on", ["full_page_writes"] = "on", ["synchronous_commit"] = "on" }, "test");

    [Fact]
    public void LiveFanout_gate_accepts_exact_dimensions_and_inclusive_latency_boundaries() => Assert.True(Valid().Passed);

    [Fact]
    public void LiveFanout_gate_rejects_reduced_workloads_missing_or_extra_delivery_and_disabled_durability()
    {
        var valid = Valid();
        foreach (var invalid in new[]
        {
            valid with { Clients = 499 }, valid with { Runs = 99 }, valid with { EventsPerSecond = 9999 },
            valid with { Events = 49999 }, valid with { PersistedEvents = 49999 }, valid with { PersistedEvents = 50001 },
            valid with { RunDeliveries = 249999 }, valid with { RunDeliveries = 250001 },
            valid with { ListCoveredUpdates = 24999999 }, valid with { ListCoveredUpdates = 25000001 },
            valid with { ProducerDurationSeconds = 5.101 }, valid with { ProducerDurationSeconds = 4.999 },
            valid with { ProducerLagP99Ms = 100.001 },
            valid with { RunLatency = valid.RunLatency with { Samples = 249999 } },
            valid with { ListLatency = valid.ListLatency with { Samples = 24999999 } },
            valid with { RunLatency = valid.RunLatency with { P95Ms = 1000.001 } },
            valid with { ListLatency = valid.ListLatency with { P99Ms = 2000.001 } },
            valid with { RunLatency = valid.RunLatency with { P50Ms = 1001 } },
            valid with { ListLatency = valid.ListLatency with { MaxMs = 1999 } },
            valid with { Durability = new() { ["fsync"] = "off", ["full_page_writes"] = "on", ["synchronous_commit"] = "on" } },
            valid with { Durability = new() { ["fsync"] = "on", ["full_page_writes"] = "on" } }
        })
            Assert.False(invalid.Passed, System.Text.Json.JsonSerializer.Serialize(invalid));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    public void LiveFanout_gate_rejects_invalid_latency_samples_even_outside_percentile_thresholds(double invalid)
    {
        var valid = Valid();
        Assert.False((valid with { RunLatency = valid.RunLatency with { P50Ms = invalid } }).Passed);
        Assert.False((valid with { RunLatency = valid.RunLatency with { P95Ms = invalid } }).Passed);
        Assert.False((valid with { ListLatency = valid.ListLatency with { P99Ms = invalid } }).Passed);
        Assert.False((valid with { ListLatency = valid.ListLatency with { MaxMs = invalid } }).Passed);
        Assert.False((valid with { ProducerDurationSeconds = invalid }).Passed);
        if (invalid != 0) Assert.False((valid with { ProducerLagP99Ms = invalid }).Passed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveFanout_actual_signed_websocket_smoke_preserves_all_durable_run_events_and_coalesced_list_coverage()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var report = await LiveFanoutBench.MeasureAsync(smoke: true, timeout.Token);
        Assert.Equal(4, report.Clients);
        Assert.Equal(2, report.Runs);
        Assert.Equal(200, report.EventsPerSecond);
        Assert.Equal(40, report.Events);
        Assert.Equal(40, report.PersistedEvents);
        Assert.Equal(80, report.RunDeliveries);
        Assert.Equal(160, report.ListCoveredUpdates);
        Assert.Equal(80, report.RunLatency.Samples);
        Assert.Equal(160, report.ListLatency.Samples);
        Assert.All(report.Durability.Values, value => Assert.Equal("on", value));
        Assert.False(report.Passed, "A reduced smoke workload must never satisfy the full acceptance gate.");
    }
}
