using System.Globalization;
using Wf.Agent.Sampling;
using Wf.Contracts.Definition;

namespace Wf.Agent.Limits;

internal sealed class CgroupV2Limiter : IResourceLimiter
{
    public string LinuxCgroupPath { get; }
    private volatile bool memoryLimited;
    private bool disposed;

    public CgroupV2Limiter(string root, TaskResourceLimits? limits = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("cgroups v2 require Linux.");
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Delegated cgroup path must be absolute.", nameof(root));
        LinuxBpfNetworkCounters.RequireCgroupV2(root);
        var enabled = File.ReadAllText(Path.Combine(root, "cgroup.subtree_control"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!enabled.Contains("cpu") || !enabled.Contains("memory"))
        {
            if (!string.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(root, "cgroup.procs"))))
                throw new InvalidOperationException("Delegated cgroup root must have no direct processes; use DelegateSubgroup=agent.");
            File.WriteAllText(Path.Combine(root, "cgroup.subtree_control"), "+cpu +memory");
        }
        LinuxCgroupPath = Path.Combine(root, $"wf-limits-{Guid.NewGuid():N}");
        LinuxBpfNetworkCounters.CreateOwnedCgroup(LinuxCgroupPath);
        try
        {
            if (limits is not null) Apply(limits);
        }
        catch { Directory.Delete(LinuxCgroupPath); throw; }
    }

    public void Apply(TaskResourceLimits limits)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (limits.CpuCores > 0)
            File.WriteAllText(Path.Combine(LinuxCgroupPath, "cpu.max"),
                $"{checked((long)Math.Floor(limits.CpuCores * 100_000)).ToString(CultureInfo.InvariantCulture)} 100000");
        memoryLimited = limits.RamMb > 0;
        if (memoryLimited)
        {
            File.WriteAllText(Path.Combine(LinuxCgroupPath, "memory.swap.max"), "0");
            File.WriteAllText(Path.Combine(LinuxCgroupPath, "memory.oom.group"), "1");
            File.WriteAllText(Path.Combine(LinuxCgroupPath, "memory.max"),
                checked(limits.RamMb * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        }
    }

    public bool MemoryLimitExceeded => memoryLimited && File.ReadLines(Path.Combine(LinuxCgroupPath, "memory.events"))
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Any(parts => parts[0] is "oom" or "oom_kill" &&
            ulong.Parse(parts[1], CultureInfo.InvariantCulture) > 0);

    public void Dispose()
    {
        if (disposed) return;
        Directory.Delete(LinuxCgroupPath);
        disposed = true;
    }
}
