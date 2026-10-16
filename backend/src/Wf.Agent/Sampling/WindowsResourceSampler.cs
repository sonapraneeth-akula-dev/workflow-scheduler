using System.Runtime.InteropServices;

namespace Wf.Agent.Sampling;

public sealed partial class WindowsResourceSampler : ResourceSampler
{
    public override ResourceCounters ReadMachine()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows counters require Windows.");
        var (total, available) = AgentTelemetry.Memory();
        var (idle, cpu) = AgentTelemetry.Cpu();
        var network = ReadNetwork(out var networkUnavailable);
        // GetSystemTimes covers only the calling processor group on machines with multiple groups.
        var cpuUnavailable = GetActiveProcessorGroupCount() > 1
            ? "Machine CPU accounting across multiple Windows processor groups is unavailable." : null;
        return new ResourceCounters(cpu - idle, cpu, total - available, network, networkUnavailable, cpuUnavailable);
    }

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetActiveProcessorGroupCount();
}
