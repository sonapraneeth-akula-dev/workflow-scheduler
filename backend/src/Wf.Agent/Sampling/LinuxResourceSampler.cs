namespace Wf.Agent.Sampling;

public sealed class LinuxResourceSampler : ResourceSampler
{
    public override ResourceCounters ReadMachine()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux counters require Linux.");
        var (total, available) = AgentTelemetry.Memory();
        var (idle, cpu) = AgentTelemetry.Cpu();
        var network = ReadNetwork(out var networkUnavailable);
        return new ResourceCounters(cpu - idle, cpu, total - available, network, networkUnavailable);
    }
}
