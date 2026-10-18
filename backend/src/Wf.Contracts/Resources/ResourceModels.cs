using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Resources;

public sealed record ResourcePoint(DateTimeOffset AtUtc, double? IntervalSeconds, double? CpuPercent,
    ulong? RamBytes, ulong? NetworkSentBytes, ulong? NetworkReceivedBytes,
    ulong? NetworkSentDeltaBytes, ulong? NetworkReceivedDeltaBytes,
    double? NetworkSentBytesPerSecond, double? NetworkReceivedBytesPerSecond,
    string CpuScope, string MemoryScope, string NetworkScope,
    string? CpuUnavailableReason, string? MemoryUnavailableReason, string? NetworkUnavailableReason,
    string? NetworkRateUnavailableReason);

public sealed record ResourceObservation(ResourcePoint Task, ResourcePoint? Machine, string? MachineUnavailableReason = null);
public sealed record ResourcePointer(string Key, string Sha256, long SizeBytes);
public sealed record ResourceChunk(ulong Sequence, ResourceObservation Observation, ResourcePointer? Previous)
{
    public int SchemaVersion { get; init; } = 1;
}
public sealed record ResourceAttemptSummary(string TaskKey, int Attempt, Guid? AgentId, long SampleCount,
    double? AverageCpuPercent, double? MaxCpuPercent, decimal? PeakRamBytes,
    decimal? NetworkSentBytes, decimal? NetworkReceivedBytes, double CpuObservedSeconds, double NetworkObservedSeconds,
    string? CpuUnavailableReason, string? MemoryUnavailableReason, string? NetworkUnavailableReason);
public sealed record ResourceSeriesSample(ulong Sequence, ResourceObservation Observation);
public sealed record ResourceSeriesPage(ResourceSeriesSample[] Samples, string? NextCursor);

public static class ResourceContract
{
    public const int MaximumObservationBytes = 16 * 1024;

    public static ResourceObservation Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > MaximumObservationBytes) throw new ArgumentException("Invalid resource sample size.");
        ResourceObservation observation;
        try
        {
            observation = JsonSerializer.Deserialize(json.Span, ResourceJsonContext.Default.ResourceObservation)
                ?? throw new ArgumentException("Missing resource observation.");
        }
        catch (JsonException error) { throw new ArgumentException("Invalid resource sample JSON.", error); }
        Validate(observation.Task, false);
        if (observation.Machine is { } machine)
        {
            Validate(machine, true);
            if (observation.MachineUnavailableReason is not null)
                throw new ArgumentException("An available machine observation cannot also be unavailable.");
        }
        else if (string.IsNullOrWhiteSpace(observation.MachineUnavailableReason) || observation.MachineUnavailableReason.Length > 512 ||
            observation.MachineUnavailableReason.Any(char.IsControl))
            throw new ArgumentException("Unavailable machine sample requires an explicit reason.");
        return observation;
    }

    private static void Validate(ResourcePoint? point, bool machine)
    {
        if (point is null || point.AtUtc.Offset != TimeSpan.Zero || point.AtUtc.Year < 2020 ||
            point.IntervalSeconds is { } interval && (!double.IsFinite(interval) || interval <= 0) ||
            point.CpuPercent is { } cpu && (!double.IsFinite(cpu) || cpu < 0 || machine && cpu > 100) ||
            point.CpuPercent is not null && point.IntervalSeconds is null ||
            point.CpuScope is not ("unavailable" or "process" or "job-with-descendants" or "cgroup-with-descendants" or "machine") ||
            point.MemoryScope is not ("unavailable" or "process-rss" or "job-resident-sum" or "cgroup-memory.current" or "physical-used") ||
            point.NetworkScope is not ("unavailable" or "host-process-etw-transport" or "cgroup-skb-l3-with-descendants" or "non-loopback-interfaces") ||
            machine != (point.CpuScope == "machine") ||
            machine != (point.MemoryScope == "physical-used") ||
            machine && point.NetworkScope is not ("unavailable" or "non-loopback-interfaces") ||
            !machine && point.NetworkScope == "non-loopback-interfaces" ||
            point.CpuPercent is not null && point.CpuScope == "unavailable" ||
            point.RamBytes is not null && point.MemoryScope == "unavailable" ||
            point.NetworkSentBytes is not null && point.NetworkScope == "unavailable")
            throw new ArgumentException("Invalid resource point, units or scope.");
        foreach (var reason in new[] { point.CpuUnavailableReason, point.MemoryUnavailableReason,
                     point.NetworkUnavailableReason, point.NetworkRateUnavailableReason })
            if (reason?.Length > 512 || reason?.Any(char.IsControl) == true)
                throw new ArgumentException("Invalid resource availability reason.");
        if (point.CpuPercent is null && string.IsNullOrWhiteSpace(point.CpuUnavailableReason) ||
            point.CpuPercent is not null && point.CpuUnavailableReason is not null ||
            point.RamBytes is null && string.IsNullOrWhiteSpace(point.MemoryUnavailableReason) ||
            point.RamBytes is not null && point.MemoryUnavailableReason is not null ||
            (point.NetworkSentBytes is null) != (point.NetworkReceivedBytes is null) ||
            point.NetworkSentBytes is null && string.IsNullOrWhiteSpace(point.NetworkUnavailableReason) ||
            point.NetworkSentBytes is not null && point.NetworkUnavailableReason is not null)
            throw new ArgumentException("Unavailable resource values require explicit reasons.");
        var rates = new[] { point.NetworkSentBytesPerSecond, point.NetworkReceivedBytesPerSecond };
        if (rates.Any(rate => rate is { } value && (!double.IsFinite(value) || value < 0)) ||
            rates.Any(rate => rate is null) != rates.All(rate => rate is null) ||
            (point.NetworkSentDeltaBytes is null) != (point.NetworkReceivedDeltaBytes is null) ||
            (point.NetworkSentDeltaBytes is null) != (point.NetworkSentBytesPerSecond is null) ||
            point.NetworkSentDeltaBytes is not null && (point.IntervalSeconds is null || point.NetworkSentBytes is null) ||
            point.NetworkSentBytesPerSecond is null && string.IsNullOrWhiteSpace(point.NetworkRateUnavailableReason) ||
            point.NetworkSentBytesPerSecond is not null && point.NetworkRateUnavailableReason is not null)
            throw new ArgumentException("Invalid resource rate interval or availability.");
        if (point.NetworkSentDeltaBytes is { } sent &&
            (sent > point.NetworkSentBytes || point.NetworkReceivedDeltaBytes > point.NetworkReceivedBytes ||
             Math.Abs(point.NetworkSentBytesPerSecond!.Value - sent / point.IntervalSeconds!.Value) >
                 Math.Max(0.000001, point.NetworkSentBytesPerSecond.Value * 0.000000001) ||
             Math.Abs(point.NetworkReceivedBytesPerSecond!.Value - point.NetworkReceivedDeltaBytes!.Value / point.IntervalSeconds.Value) >
                 Math.Max(0.000001, point.NetworkReceivedBytesPerSecond.Value * 0.000000001)))
            throw new ArgumentException("Resource rates do not match byte deltas and duration.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResourceObservation))]
[JsonSerializable(typeof(ResourceChunk))]
[JsonSerializable(typeof(ResourceAttemptSummary[]))]
[JsonSerializable(typeof(ResourceSeriesPage))]
[JsonSerializable(typeof(ResourcePointer))]
public sealed partial class ResourceJsonContext : JsonSerializerContext;
