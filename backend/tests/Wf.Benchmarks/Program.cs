using BenchmarkDotNet.Running;
using Wf.Benchmarks;

var summaries = BenchmarkSwitcher.FromAssembly(typeof(DescribeBenchmarks).Assembly).Run(args);
var reports = summaries.SelectMany(s => s.Reports).ToArray();
if (reports.Length == 0 || reports.Any(r => r.ResultStatistics is null))
{
    Console.Error.WriteLine("Benchmark failed or produced no measurements.");
    return 1;
}
var passed = true;
foreach (var report in reports)
{
    var p95 = report.ResultStatistics!.Percentiles.P95;
    var withinLimit = double.IsFinite(p95) && p95 < 1_000_000_000;
    Console.WriteLine($"{report.BenchmarkCase.Descriptor.WorkloadMethod.Name}: p95={p95 / 1_000_000:F3} ms; " +
        $"limit < 1000 ms; {(withinLimit ? "PASS" : "FAIL")}");
    passed &= withinLimit;
}
return passed ? 0 : 1;
