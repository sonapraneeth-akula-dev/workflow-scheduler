using System.Runtime.InteropServices;

namespace Wf.Agent.Sampling;

public sealed partial class WindowsResourceSampler(NativeSamplingOptions? options = null) : ResourceSampler(options)
{
    private readonly Lazy<WindowsNetworkTrace> trace = new(() => new WindowsNetworkTrace());
    protected override ITaskResourceScope CreateScope() => new WindowsTaskResourceScope(() => trace.Value);
    public override void Dispose() { if (trace.IsValueCreated) trace.Value.Dispose(); }

    public override ResourceCounters ReadMachine()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows counters require Windows.");
        var (total, available) = AgentTelemetry.Memory();
        var (idle, cpu) = AgentTelemetry.Cpu();
        var network = ReadNetwork(out var networkUnavailable);
        // GetSystemTimes covers only the calling processor group on machines with multiple groups.
        var cpuUnavailable = GetActiveProcessorGroupCount() > 1
            ? "Machine CPU accounting across multiple Windows processor groups is unavailable." : null;
        return new ResourceCounters(cpu - idle, cpu, total - available, network, networkUnavailable, cpuUnavailable,
            CpuScope: "machine", MemoryScope: "physical-used", NetworkScope: "non-loopback-interfaces");
    }

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetActiveProcessorGroupCount();
}
