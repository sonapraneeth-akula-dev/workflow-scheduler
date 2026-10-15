using BenchmarkDotNet.Running;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wf.Benchmarks;

if (args.Length > 0 && args[0] == "--compare")
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: --compare <baseline.json> <current.json>");
        return 1;
    }
    try
    {
        var baseline = JsonSerializer.Deserialize<EngineBenchmarkMetrics>(File.ReadAllText(args[1])) ??
            throw new JsonException("Baseline metrics are missing.");
        var current = JsonSerializer.Deserialize<EngineBenchmarkMetrics>(File.ReadAllText(args[2])) ??
            throw new JsonException("Current metrics are missing.");
        var errors = PerformanceGate.Compare(baseline, current);
        foreach (var error in errors)
            Console.Error.WriteLine(error);
        if (errors.IsEmpty)
            Console.WriteLine("PASS: run and per-node p95 remain within 10% of the same-runner baseline.");
        return errors.IsEmpty ? 0 : 1;
    }
    catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Cannot read benchmark metrics: {error.Message}");
        return 1;
    }
}
string? metricsPath = null;
var benchmarkArgs = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (args[index] != "--metrics")
        benchmarkArgs.Add(args[index]);
    else if (metricsPath is not null || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Supply --metrics <path> exactly once.");
        return 1;
    }
    else
        metricsPath = args[index];
}
var summaries = BenchmarkSwitcher.FromAssembly(typeof(DescribeBenchmarks).Assembly).Run(benchmarkArgs.ToArray());
var reports = summaries.SelectMany(s => s.Reports).ToArray();
if (reports.Length == 0 || reports.Any(r => r.ResultStatistics is null))
{
    Console.Error.WriteLine("Benchmark failed or produced no measurements.");
    return 1;
}
var passed = true;
foreach (var report in reports)
{
    var statistics = report.ResultStatistics ?? throw new InvalidOperationException("Benchmark measurements are missing.");
    var p95 = statistics.Percentiles.P95;
    var engine = report.BenchmarkCase.Descriptor.Type == typeof(EngineCoreBenchmarks);
    var withinLimit = double.IsFinite(p95) && p95 > 0 && (engine ? p95 <= 3_000_000_000 : p95 < 1_000_000_000);
    Console.WriteLine($"{report.BenchmarkCase.Descriptor.WorkloadMethod.Name}: p95={p95 / 1_000_000:F3} ms; " +
        $"limit {(engine ? "<= 3000" : "< 1000")} ms; {(withinLimit ? "PASS" : "FAIL")}");
    passed &= withinLimit;
}
var engineReports = reports.Where(r => r.BenchmarkCase.Descriptor.Type == typeof(EngineCoreBenchmarks)).ToArray();
if (engineReports.Length == 1)
{
    var samples = await new EngineCoreBenchmarks().MeasureNodeLatenciesAsync();
    Array.Sort(samples);
    var metrics = new EngineBenchmarkMetrics(
        $"{RuntimeInformation.OSDescription}|{RuntimeInformation.FrameworkDescription}|{RuntimeInformation.ProcessArchitecture}|{Environment.ProcessorCount}",
        engineReports[0].ResultStatistics?.Percentiles.P95 ?? throw new InvalidOperationException("Engine measurements are missing."),
        samples[(int)Math.Ceiling(samples.Length * 0.95) - 1], samples.Length);
    var errors = PerformanceGate.Validate(metrics);
    foreach (var error in errors)
        Console.Error.WriteLine(error);
    passed &= errors.IsEmpty;
    Console.WriteLine($"Node Ready-to-terminal: p95={metrics.NodeP95Nanoseconds / 1_000_000:F3} ms; samples={metrics.NodeSamples}");
    metricsPath ??= Path.Combine("BenchmarkDotNet.Artifacts", "engine-metrics.json");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(metricsPath))!);
    File.WriteAllText(metricsPath, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
}
else if (engineReports.Length > 1 || metricsPath is not null)
{
    Console.Error.WriteLine("Engine metrics require exactly one engine benchmark job.");
    return 1;
}
return passed ? 0 : 1;
