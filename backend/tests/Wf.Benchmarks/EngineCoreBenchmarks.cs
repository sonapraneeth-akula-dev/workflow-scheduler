using BenchmarkDotNet.Attributes;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[SimpleJob(launchCount: 1, warmupCount: 5, iterationCount: 20, invocationCount: 4)]
public class EngineCoreBenchmarks
{
    private readonly WorkflowDefinition workflow = Workflow.Define("benchmark-engine", "1", w =>
    {
        w.Type("benchmark");
        for (var index = 0; index < 200; index++)
            w.Node($"node-{index}", n =>
            {
                if (index > 0)
                    n.DependsOn($"node-{index - 1}");
                n.Task("work", _ => { });
            });
    });

    [Benchmark]
    public Task<LocalRunResult> Run200Nodes() => new LocalEngine(workflow, output: TextWriter.Null).RunAsync();

    public async Task<double[]> MeasureNodeLatenciesAsync()
    {
        for (var warmup = 0; warmup < 3; warmup++)
            await Run200Nodes();
        var samples = new List<double>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var result = await Run200Nodes();
            if (result.Snapshot.Status != RunStatus.Succeeded || result.Snapshot.Nodes.Count != 200)
                throw new InvalidOperationException("The benchmark did not successfully execute all 200 nodes.");
            foreach (var node in result.Snapshot.Nodes.Keys)
            {
                var ready = result.Events.Single(e => e.Entity == EventEntity.Node && e.Key == node &&
                    e.State == (int)ExecutionStatus.Ready);
                var ended = result.Events.Single(e => e.Entity == EventEntity.Node && e.Key == node &&
                    e.State == (int)ExecutionStatus.Succeeded);
                samples.Add((ended.At - ready.At).Ticks * 100d);
            }
        }
        return samples.ToArray();
    }
}
