namespace Wf.Agent.Sampling;

public sealed class LinuxResourceSampler(NativeSamplingOptions? options = null) : ResourceSampler(options)
{
    internal override void PrepareUnscopedStart(System.Diagnostics.ProcessStartInfo startInfo)
    {
        if (Options.Enabled) LinuxTaskResourceScope.PrepareLaunch(startInfo, null);
    }

    protected override ITaskResourceScope CreateScope() => new LinuxTaskResourceScope(Options.LinuxCgroupRoot ??
        throw new InvalidOperationException("Agent:Sampling:LinuxCgroupRoot must name an owned, delegated cgroup-v2 subtree."));

    public override ResourceCounters ReadMachine()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux counters require Linux.");
        var (total, available) = AgentTelemetry.Memory();
        var (idle, cpu) = AgentTelemetry.Cpu();
        var network = ReadNetwork(out var networkUnavailable);
        return new ResourceCounters(cpu - idle, cpu, total - available, network, networkUnavailable,
            CpuScope: "machine", MemoryScope: "physical-used", NetworkScope: "non-loopback-interfaces");
    }
}
