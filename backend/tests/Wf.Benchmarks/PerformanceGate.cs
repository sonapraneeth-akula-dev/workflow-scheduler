using System.Collections.Immutable;

namespace Wf.Benchmarks;

public sealed record EngineBenchmarkMetrics(string EnvironmentKey, double RunP95Nanoseconds,
    double NodeP95Nanoseconds, int NodeSamples, int SchemaVersion = 1);

public static class PerformanceGate
{
    public static bool ValidatorMeanWithinLimit(double nanoseconds) =>
        double.IsFinite(nanoseconds) && nanoseconds > 0 && nanoseconds < 2_000_000_000;

    public static ImmutableArray<string> Validate(EngineBenchmarkMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var errors = ImmutableArray.CreateBuilder<string>();
        if (metrics.SchemaVersion != 1 || string.IsNullOrWhiteSpace(metrics.EnvironmentKey) || metrics.NodeSamples != 1000)
            errors.Add("Metrics require schema 1, an environment fingerprint and exactly 1000 node samples.");
        if (!double.IsFinite(metrics.RunP95Nanoseconds) || metrics.RunP95Nanoseconds <= 0 || metrics.RunP95Nanoseconds > 3_000_000_000)
            errors.Add("200-node run p95 must be positive and <= 3 seconds.");
        if (!double.IsFinite(metrics.NodeP95Nanoseconds) || metrics.NodeP95Nanoseconds <= 0 || metrics.NodeP95Nanoseconds > 10_000_000)
            errors.Add("Ready-to-terminal node p95 must be positive and <= 10 milliseconds.");
        return errors.ToImmutable();
    }

    public static ImmutableArray<string> Compare(EngineBenchmarkMetrics baseline, EngineBenchmarkMetrics current)
    {
        var errors = Validate(baseline).Select(e => $"Baseline: {e}")
            .Concat(Validate(current).Select(e => $"Current: {e}")).ToImmutableArray().ToBuilder();
        if (!string.Equals(baseline.EnvironmentKey, current.EnvironmentKey, StringComparison.Ordinal))
            errors.Add("Baseline and current metrics must come from the same OS/runtime/architecture/CPU-count environment.");
        if (errors.Count != 0)
            return errors.ToImmutable();
        if (current.RunP95Nanoseconds / baseline.RunP95Nanoseconds > 1.10)
            errors.Add("200-node run p95 regressed by more than 10%.");
        if (current.NodeP95Nanoseconds / baseline.NodeP95Nanoseconds > 1.10)
            errors.Add("Ready-to-terminal node p95 regressed by more than 10%.");
        return errors.ToImmutable();
    }
}
