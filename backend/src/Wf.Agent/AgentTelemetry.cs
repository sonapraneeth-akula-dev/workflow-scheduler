using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent;

public interface IAgentTelemetry
{
    Hello CreateHello(AgentOptions options);
    Heartbeat Sample();
}

public sealed partial class AgentTelemetry(TimeProvider timeProvider, TaskCommands? tasks = null) : IAgentTelemetry
{
    private (ulong Idle, ulong Total)? previousCpu;

    public Hello CreateHello(AgentOptions options)
    {
        var (total, _) = Memory();
        return new Hello
        {
            Id = options.Id, Name = options.Name, Pool = options.Pool, MaxTasks = options.MaxTasks,
            Os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux"
                : throw new PlatformNotSupportedException("Agents support Windows and Linux."),
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Cores = (uint)Environment.ProcessorCount, RamMb = total / (1024 * 1024),
            Version = typeof(AgentTelemetry).Assembly.GetName().Version!.ToString(),
            ProtocolVersion = 1, SupportsCalibrationCommands = true, SupportsDrainCommands = true,
            Labels = { options.Labels.ToDictionary() }, Inflight = { tasks?.Inflight ?? [] }
        };
    }

    public Heartbeat Sample()
    {
        var (total, available) = Memory();
        var cpu = Cpu();
        var percent = previousCpu is { } previous && cpu.Total > previous.Total
            ? 100 * (1 - (double)(cpu.Idle - previous.Idle) / (cpu.Total - previous.Total)) : 0;
        previousCpu = cpu;
        return new Heartbeat
        {
            At = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
            CpuPercent = Math.Clamp(percent, 0, 100), RamUsedMb = (total - available) / (1024 * 1024),
            DiskFreeMb = (ulong)new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory)!).AvailableFreeSpace / (1024 * 1024),
            SlotsUsed = (uint)(tasks?.Inflight.Count ?? 0)
        };
    }

    internal static (ulong Total, ulong Available) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return (memory.TotalPhysical, memory.AvailablePhysical);
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Agents support Windows and Linux.");
        var values = File.ReadLines("/proc/meminfo")
            .Where(line => line.StartsWith("MemTotal:", StringComparison.Ordinal) || line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => ulong.Parse(parts[1], CultureInfo.InvariantCulture) * 1024);
        return (values["MemTotal:"], values["MemAvailable:"]);
    }

    internal static (ulong Idle, ulong Total) Cpu()
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return (idle, kernel + user);
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Agents support Windows and Linux.");
        var parts = File.ReadLines("/proc/stat").First().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var values = parts.Skip(1).Take(8).Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return (values[3] + values[4], values.Aggregate(0UL, (sum, value) => sum + value));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
