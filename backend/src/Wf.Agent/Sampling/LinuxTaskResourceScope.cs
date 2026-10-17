using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Wf.Agent.Sampling;

internal sealed class LinuxTaskResourceScope : ITaskResourceScope
{
    private readonly string directory;
    private readonly string supervisor;
    internal string DirectoryPath => directory;
    private readonly LinuxBpfNetworkCounters? network;
    private readonly string? networkUnavailable;
    private NetworkBytes baseline = new(0, 0);
    private bool disposed;

    public LinuxTaskResourceScope(string delegatedRoot)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux cgroups require Linux.");
        if (!Path.IsPathFullyQualified(delegatedRoot)) throw new InvalidOperationException("Delegated cgroup root must be absolute.");
        LinuxBpfNetworkCounters.RequireCgroupV2(delegatedRoot);
        var enabled = File.ReadAllText(Path.Combine(delegatedRoot, "cgroup.subtree_control")).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!enabled.Contains("cpu") || !enabled.Contains("memory"))
        {
            if (!string.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(delegatedRoot, "cgroup.procs"))))
                throw new InvalidOperationException("Delegated root must have no direct processes; use DelegateSubgroup=agent and Delegate=cpu memory.");
            File.WriteAllText(Path.Combine(delegatedRoot, "cgroup.subtree_control"), "+cpu +memory");
        }
        directory = Path.Combine(delegatedRoot, $"wf-task-{Guid.NewGuid():N}");
        supervisor = Path.Combine(directory, "supervisor");
        LinuxBpfNetworkCounters.CreateOwnedCgroup(directory);
        try
        {
            if (!File.Exists(Path.Combine(directory, "memory.current")))
                throw new InvalidOperationException("Delegated cgroup must expose cpu.stat and memory.current.");
            File.WriteAllText(Path.Combine(directory, "cgroup.subtree_control"), "+cpu +memory");
            LinuxBpfNetworkCounters.CreateOwnedCgroup(supervisor);
            try { network = new LinuxBpfNetworkCounters(directory); }
            catch (Exception exception) when (exception is Win32Exception or PlatformNotSupportedException)
            {
                networkUnavailable = exception.Message;
            }
        }
        catch
        {
            if (Directory.Exists(supervisor)) Directory.Delete(supervisor);
            Directory.Delete(directory);
            throw;
        }
    }

    internal LinuxTaskResourceScope(string existingDirectory, Process process)
    {
        directory = existingDirectory;
        supervisor = Path.Combine(directory, "supervisor");
        LinuxBpfNetworkCounters.RequireCgroupV2(directory);
        if (!File.ReadLines(Path.Combine(supervisor, "cgroup.procs")).Contains(
            process.Id.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal))
            throw new InvalidDataException("Recovered supervisor is outside its resource accounting cgroup.");
        networkUnavailable = "Agent restarted; previous BPF network counters cannot be resumed.";
    }

    public void PrepareStart(ProcessStartInfo startInfo) => PrepareLaunch(startInfo, supervisor);

    internal static void PrepareLaunch(ProcessStartInfo startInfo, string? directory)
    {
        if (startInfo.UseShellExecute || startInfo.Arguments.Length != 0)
            throw new InvalidOperationException("Native cgroup launch requires UseShellExecute=false and ArgumentList.");
        if (!File.Exists("/usr/bin/setpriv"))
            throw new InvalidOperationException("Native sampling requires /usr/bin/setpriv to avoid passing the agent's BPF/network capabilities to tasks.");
        var executable = startInfo.FileName;
        var arguments = startInfo.ArgumentList.ToArray();
        var capabilities = File.ReadLines("/proc/self/status").First(line => line.StartsWith("CapEff:", StringComparison.Ordinal))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
        startInfo.FileName = "/usr/bin/setpriv";
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("--inh-caps=-all");
        startInfo.ArgumentList.Add("--ambient-caps=-all");
        startInfo.ArgumentList.Add("--no-new-privs");
        if ((ulong.Parse(capabilities, NumberStyles.HexNumber, CultureInfo.InvariantCulture) & (1UL << 8)) != 0)
            startInfo.ArgumentList.Add("--bounding-set=-all");
        startInfo.ArgumentList.Add("--");
        if (directory is not null)
            foreach (var value in new[] { "/bin/sh", "-c", "printf '%s' \"$$\" > \"$1/cgroup.procs\" || exit 125; shift; exec \"$@\"",
                         "wf-task-scope", directory })
                startInfo.ArgumentList.Add(value);
        foreach (var value in new[] { executable }.Concat(arguments))
            startInfo.ArgumentList.Add(value);
    }

    public void Bind(Process process)
    {
        var started = Stopwatch.GetTimestamp();
        while (!File.ReadLines(Path.Combine(supervisor, "cgroup.procs")).Any(line =>
                   int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid == process.Id))
        {
            if (process.HasExited || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2))
                throw new InvalidOperationException("Task host did not join its owned cgroup before exec.");
            Thread.Sleep(5);
        }
    }

    public void BeginAttempt() => baseline = network?.Read() ?? new NetworkBytes(0, 0);
    public void EndAttempt() { }

    public ResourceCounters ReadCounters()
    {
        var cpu = File.ReadLines(Path.Combine(directory, "cpu.stat"))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .First(parts => parts[0] == "usage_usec")[1];
        var memory = ulong.Parse(File.ReadAllText(Path.Combine(directory, "memory.current")).Trim(), CultureInfo.InvariantCulture);
        var counts = network?.Read();
        return new ResourceCounters(checked(ulong.Parse(cpu, CultureInfo.InvariantCulture) * 10),
            (ulong)Stopwatch.GetElapsedTime(0).Ticks, memory,
            counts is null ? null : new Dictionary<string, NetworkBytes>
            {
                ["cgroup-skb"] = new(counts.Sent - baseline.Sent, counts.Received - baseline.Received)
            }, networkUnavailable, CpuScope: "cgroup-with-descendants", MemoryScope: "cgroup-memory.current",
            NetworkScope: "cgroup-skb-l3-with-descendants");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { network?.Dispose(); }
        finally { Directory.Delete(supervisor); Directory.Delete(directory); }
    }
}
