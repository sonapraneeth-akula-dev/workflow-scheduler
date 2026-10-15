using BenchmarkDotNet.Attributes;
using Wf.Sdk;

namespace Wf.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class DescribeBenchmarks
{
    private readonly WorkflowDefinition workflow = Workflow.Define("benchmark-describe", "1", w =>
    {
        w.Type("benchmark").Metadata("area", "description").Variables(v => v.String("env", "dev"));
        for (var index = 0; index < 200; index++)
            w.Node($"node-{index}", n =>
            {
                if (index > 0)
                    n.DependsOn($"node-{index - 1}");
                n.Task("work", _ => { });
            });
    });

    [Benchmark]
    public byte[] Describe200Nodes() => ManifestWriter.WriteUtf8(workflow);
}
