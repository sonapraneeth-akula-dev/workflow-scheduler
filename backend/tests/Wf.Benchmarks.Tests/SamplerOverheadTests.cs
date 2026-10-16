using Wf.Contracts.Resources;

namespace Wf.Benchmarks.Tests;

public sealed class SamplerOverheadTests
{
    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(.3, 30, 1)]
    [InlineData(30, 30, 100)]
    [InlineData(60, 30, 200)]
    public void CpuIsOneCoreProcessTimeNotMachineNormalized(double cpu, double wall, double expected) =>
        Assert.Equal(expected, SamplerOverheadBenchmarks.OneCoreCpuPercent(cpu, wall), 9);

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(double.NaN, 30)]
    [InlineData(double.PositiveInfinity, 30)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    public void InvalidCpuOrWallIsRejectedNotZeroed(double cpu, double wall) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SamplerOverheadBenchmarks.OneCoreCpuPercent(cpu, wall));

    [Fact]
    public void GateUsesEverySignedPairAndInclusiveBoundary()
    {
        Assert.True(SamplerOverheadBenchmarks.WithinLimit([-1, 0, 1]));
        Assert.False(SamplerOverheadBenchmarks.WithinLimit([-.9, -.8, 1.000001]));
        Assert.Throws<ArgumentException>(() => SamplerOverheadBenchmarks.WithinLimit([]));
        Assert.Throws<ArgumentException>(() => SamplerOverheadBenchmarks.WithinLimit([0, 1]));
        Assert.Throws<ArgumentException>(() => SamplerOverheadBenchmarks.WithinLimit([0, 0, double.NaN]));
    }

    [Fact]
    public void DistributionKeepsNegativeValuesAndReportsNearestRankP95()
    {
        Assert.Equal(new SamplerOverheadBenchmarks.Distribution(-2, .5, 4, 4),
            SamplerOverheadBenchmarks.Summarize([4, -2, 0, 1]));
        Assert.Throws<ArgumentException>(() => SamplerOverheadBenchmarks.Summarize([]));
        Assert.Throws<ArgumentException>(() => SamplerOverheadBenchmarks.Summarize([double.PositiveInfinity]));
    }

    [Fact]
    public void NativeCompletenessCannotPassOnFallbackNullZeroRamOrUnobservedNetwork()
    {
        var task = CompleteTask();
        var observation = new ResourceObservation(task, task with
            { CpuScope = "machine", MemoryScope = "physical-used", NetworkScope = "non-loopback-interfaces" });
        SamplerOverheadBenchmarks.ValidateObservation(observation);
        foreach (var invalid in new[]
        {
            task with { CpuPercent = null, CpuUnavailableReason = "unavailable" },
            task with { CpuPercent = double.NaN },
            task with { RamBytes = 0 },
            task with { NetworkSentBytes = null },
            task with { NetworkReceivedDeltaBytes = null },
            task with { NetworkSentBytesPerSecond = double.PositiveInfinity },
            task with { NetworkUnavailableReason = "lost native events" },
            task with { CpuScope = "process" },
            task with { NetworkScope = "unavailable" },
            task with { IntervalSeconds = null },
            task with { IntervalSeconds = .01 },
            task with { IntervalSeconds = 5.001 }
        })
            Assert.Throws<InvalidDataException>(() => SamplerOverheadBenchmarks.ValidateObservation(observation with { Task = invalid }));
        Assert.Throws<InvalidDataException>(() => SamplerOverheadBenchmarks.ValidateObservation(observation with { Machine = null }));
        Assert.Throws<InvalidDataException>(() => SamplerOverheadBenchmarks.ValidateObservation(observation with
            { Machine = observation.Machine! with { CpuPercent = null } }));
    }

    [Fact]
    public void ConfigurationRejectsCondensedRunsInsufficientPairsAndNoWarmup()
    {
        var valid = new SamplerOverheadBenchmarks.Options("owned-artifacts", CgroupRoot: Path.GetFullPath("owned-cgroup"));
        SamplerOverheadBenchmarks.ValidateOptions(valid);
        foreach (var invalid in new[]
        {
            valid with { Pairs = 2 }, valid with { Seconds = 1 }, valid with { WarmupSeconds = 0 },
            valid with { Concurrency = 0 }, valid with { Concurrency = 17 }, valid with { Directory = "" }
        })
            Assert.Throws<ArgumentException>(() => SamplerOverheadBenchmarks.ValidateOptions(invalid));
    }

    [Fact]
    public void WorkloadMustActuallyRunForTheEntireWindowAtItsPacedTrafficRate()
    {
        var valid = new SamplerOverheadBenchmarks.WorkloadResult(800, 819200, 819200, 40, []);
        SamplerOverheadBenchmarks.ValidateWorkload(valid, 40);
        foreach (var invalid in new[]
        {
            valid with { Cycles = 0, SentBytes = 0, ReceivedBytes = 0 },
            valid with { WallSeconds = 0 }, valid with { WallSeconds = double.NaN },
            valid with { Cycles = 1000 }, valid with { SentBytes = 819199 },
            valid with { ReceivedBytes = 819199 }
        })
            Assert.Throws<InvalidDataException>(() => SamplerOverheadBenchmarks.ValidateWorkload(invalid, 40));
    }

    private static ResourcePoint CompleteTask() => new(DateTimeOffset.UtcNow, 1, 20, 64 * 1024 * 1024,
        4096, 4096, 1024, 1024, 1024, 1024, "job-with-descendants", "job-resident-sum",
        "host-process-etw-transport", null, null, null, null);
}
