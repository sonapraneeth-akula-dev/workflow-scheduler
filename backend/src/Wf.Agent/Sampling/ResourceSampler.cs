using System.Diagnostics;
using System.ComponentModel;
using System.Net.NetworkInformation;

namespace Wf.Agent.Sampling;

public sealed record NetworkBytes(ulong Sent, ulong Received);

public sealed record ResourceCounters(ulong CpuBusy, ulong CpuTotal, ulong RamBytes,
    IReadOnlyDictionary<string, NetworkBytes>? Network, string? NetworkUnavailableReason = null,
    string? CpuUnavailableReason = null, string CpuScope = "process", string MemoryScope = "process-rss",
    string NetworkScope = "unavailable");

public sealed record ResourceMeasurement(DateTimeOffset At, double? CpuPercent, ulong? RamBytes,
    NetworkBytes? Network, double? BytesSentPerSecond, double? BytesReceivedPerSecond,
    string? NetworkUnavailableReason, string? CpuUnavailableReason = null, string? Error = null,
    string CpuScope = "process", string MemoryScope = "process-rss", string NetworkScope = "unavailable",
    TimeSpan? Interval = null);

public abstract class ResourceSampler(NativeSamplingOptions? options = null) : IDisposable
{
    public const string ProcessNetworkUnavailable =
        "Native per-process network byte accounting is unavailable; dedicated kernel network telemetry is required.";

    protected NativeSamplingOptions Options { get; } = options ?? new();
    public bool NativeEnabled => Options.Enabled;

    public static ResourceSampler Create(NativeSamplingOptions? options = null) => OperatingSystem.IsWindows() ? new WindowsResourceSampler(options)
        : OperatingSystem.IsLinux() ? new LinuxResourceSampler(options)
        : throw new PlatformNotSupportedException("Resource sampling supports Windows and Linux.");

    public abstract ResourceCounters ReadMachine();

    public ITaskResourceScope? TryCreateScope(out string? unavailableReason)
    {
        unavailableReason = null;
        if (!Options.Enabled)
        {
            unavailableReason = ProcessNetworkUnavailable + " Enable Agent:Sampling:Enabled for native accounting.";
            return null;
        }
        try { return CreateScope(); }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or
            InvalidOperationException or PlatformNotSupportedException)
        {
            unavailableReason = "Native task resource scope unavailable: " + exception.Message;
            return null;
        }
    }

    protected virtual ITaskResourceScope CreateScope() => throw new PlatformNotSupportedException("Native task accounting is unsupported.");
    internal virtual void PrepareUnscopedStart(ProcessStartInfo startInfo) { }
    public virtual void Dispose() { }

    public virtual ResourceCounters ReadProcess(Process process)
    {
        process.Refresh();
        if (process.HasExited) throw new InvalidOperationException("Task process has exited.");
        return new ResourceCounters((ulong)process.TotalProcessorTime.Ticks,
            (ulong)Stopwatch.GetElapsedTime(0).Ticks, (ulong)process.WorkingSet64, null, ProcessNetworkUnavailable);
    }

    protected static IReadOnlyDictionary<string, NetworkBytes>? ReadNetwork(out string? unavailableReason)
    {
        unavailableReason = null;
        try
        {
            var result = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToDictionary(adapter => adapter.Id, adapter =>
                {
                    var stats = adapter.GetIPStatistics();
                    return new NetworkBytes(checked((ulong)stats.BytesSent), checked((ulong)stats.BytesReceived));
                });
            if (result.Count > 0) return result;
            unavailableReason = "No non-loopback network interfaces are visible.";
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            unavailableReason = exception.Message;
        }
        return null;
    }

    public static ResourceMeasurement Measure(ResourceCounters current, ResourceCounters? previous,
        TimeSpan elapsed, DateTimeOffset at, bool machine)
    {
        double? cpu = null;
        if (current.CpuUnavailableReason is null && previous is { CpuUnavailableReason: null } &&
            current.CpuTotal > previous.CpuTotal && current.CpuBusy >= previous.CpuBusy)
        {
            var percent = 100.0 * (current.CpuBusy - previous.CpuBusy) / (current.CpuTotal - previous.CpuTotal);
            cpu = machine ? Math.Clamp(percent, 0, 100) : percent;
        }
        NetworkBytes? network = null;
        double? sentRate = null, receivedRate = null;
        if (current.Network is { } interfaces)
        {
            network = new NetworkBytes(interfaces.Values.Aggregate(0UL, (sum, value) => checked(sum + value.Sent)),
                interfaces.Values.Aggregate(0UL, (sum, value) => checked(sum + value.Received)));
            if (elapsed > TimeSpan.Zero && previous?.Network is { } old && interfaces.Count == old.Count &&
                interfaces.All(pair => old.TryGetValue(pair.Key, out var value) &&
                    pair.Value.Sent >= value.Sent && pair.Value.Received >= value.Received))
            {
                sentRate = interfaces.Sum(pair => (double)(pair.Value.Sent - old[pair.Key].Sent)) / elapsed.TotalSeconds;
                receivedRate = interfaces.Sum(pair => (double)(pair.Value.Received - old[pair.Key].Received)) / elapsed.TotalSeconds;
            }
        }
        return new ResourceMeasurement(at, cpu, current.RamBytes, network, sentRate, receivedRate,
            current.NetworkUnavailableReason, current.CpuUnavailableReason, CpuScope: current.CpuScope,
            MemoryScope: current.MemoryScope, NetworkScope: current.NetworkScope, Interval: previous is null ? null : elapsed);
    }
}
