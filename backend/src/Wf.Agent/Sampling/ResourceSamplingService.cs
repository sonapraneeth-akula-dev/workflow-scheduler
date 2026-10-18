using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Resources;

namespace Wf.Agent.Sampling;

public sealed record TaskResourceSample(string RunId, string NodeKey, string TaskKey, uint Attempt,
    int ProcessId, DateTimeOffset? ProcessStartedAt, ResourceMeasurement Sample);

public sealed class ResourceSamplingService : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(5);
    private readonly ResourceSampler sampler;
    private readonly TimeProvider clock;
    private readonly ILogger<ResourceSamplingService> logger;
    private readonly TimeSpan interval;
    private readonly object gate = new();
    private readonly List<Tracking> tasks = [];
    private ResourceCounters? machineCounters;
    private long machineTimestamp;
    private ResourceMeasurement? latestMachine;

    public ResourceSamplingService(ResourceSampler sampler, TimeProvider clock, ILogger<ResourceSamplingService> logger,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        this.interval = interval ?? DefaultInterval;
        if (this.interval <= TimeSpan.Zero || this.interval > MaximumInterval)
            throw new ArgumentOutOfRangeException(nameof(interval), "Sampling interval must be positive and at most five seconds.");
        this.sampler = sampler;
        this.clock = clock;
        this.logger = logger;
    }

    public ResourceMeasurement? LatestMachine { get { lock (gate) return latestMachine; } }
    public IReadOnlyList<TaskResourceSample> ActiveTasks
    {
        get { lock (gate) return tasks.Select(task => task.Latest!).ToArray(); }
    }

    internal ITaskResourceScope? CreateProcessScope(out string? unavailableReason)
    {
        var scope = sampler.TryCreateScope(out unavailableReason);
        if (scope is null && sampler.NativeEnabled)
            logger.LogWarning("Native task resource accounting unavailable: {Reason}", unavailableReason);
        return scope;
    }

    internal void PrepareUnscopedStart(ProcessStartInfo startInfo) => sampler.PrepareUnscopedStart(startInfo);

    public IDisposable Track(Process process, AttemptRef attempt, ITaskResourceScope? scope = null,
        string? nativeUnavailableReason = null, Action<AgentMessage>? publish = null, ulong reservationEpoch = 0,
        ulong initialSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(attempt);
        lock (gate)
        {
            var tracking = new Tracking(this, process, attempt.Clone(), scope, nativeUnavailableReason, publish, reservationEpoch)
                { Sequence = initialSequence };
            try { scope?.BeginAttempt(); }
            catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                tracking.Scope = null;
                tracking.NativeUnavailableReason = exception.Message;
            }
            SampleTask(tracking);
            tasks.Add(tracking);
            return tracking;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            do
            {
                lock (gate)
                {
                    latestMachine = Read(() => sampler.ReadMachine(), ref machineCounters, ref machineTimestamp, machine: true);
                    foreach (var task in tasks) SampleTask(task);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void SampleTask(Tracking tracking)
    {
        var sample = Read(() =>
        {
            if (tracking.Scope is not null)
            {
                tracking.StartedAt ??= new DateTimeOffset(tracking.Process.StartTime.ToUniversalTime());
                var scoped = tracking.Scope.ReadCounters();
                return tracking.NativeUnavailableReason is null ? scoped : scoped with
                {
                    Network = null, NetworkUnavailableReason = tracking.NativeUnavailableReason, NetworkScope = "unavailable"
                };
            }
            tracking.Process.Refresh();
            if (tracking.Process.HasExited) throw new InvalidOperationException("Task process has exited.");
            var started = new DateTimeOffset(tracking.Process.StartTime.ToUniversalTime());
            if (tracking.StartedAt is { } expected && expected != started)
                throw new InvalidOperationException("Task process identity changed; refusing a reused PID.");
            tracking.StartedAt = started;
            var counters = sampler.ReadProcess(tracking.Process);
            return tracking.NativeUnavailableReason is null ? counters
                : counters with { NetworkUnavailableReason = tracking.NativeUnavailableReason };
        }, ref tracking.Counters, ref tracking.Timestamp, machine: false);
        var attempt = tracking.Attempt;
        tracking.Latest = new TaskResourceSample(attempt.RunId, attempt.NodeKey, attempt.TaskKey, attempt.Attempt,
            tracking.Process.Id, tracking.StartedAt, sample);
        if (tracking.Publish is not null)
        {
            var observation = new ResourceObservation(Point(sample), latestMachine is { } machine ? Point(machine, true) : null,
                latestMachine is null ? "Machine sampling has not produced its first observation." : null);
            var json = JsonSerializer.SerializeToUtf8Bytes(observation, ResourceJsonContext.Default.ResourceObservation);
            _ = ResourceContract.Parse(json);
            tracking.Publish(new AgentMessage { ResourceSample = new ResourceSample
            {
                Task = attempt.Clone(), ReservationEpoch = tracking.ReservationEpoch,
                SampleSequence = checked(++tracking.Sequence), SampleJson = ByteString.CopyFrom(json)
            } });
        }
    }

    private static ResourcePoint Point(ResourceMeasurement sample, bool machine = false)
    {
        static string? Reason(string? reason) => reason is null ? null :
            new string(reason.Where(character => !char.IsControl(character)).Take(512).ToArray());
        return new(sample.At.ToUniversalTime(), sample.Interval is { } interval && interval > TimeSpan.Zero ? interval.TotalSeconds : null,
            sample.CpuPercent, sample.RamBytes, sample.Network?.Sent, sample.Network?.Received,
            sample.NetworkDelta?.Sent, sample.NetworkDelta?.Received, sample.BytesSentPerSecond, sample.BytesReceivedPerSecond,
            machine ? "machine" : sample.Error is not null ? "unavailable" : sample.CpuScope,
            machine ? "physical-used" : sample.Error is not null ? "unavailable" : sample.MemoryScope, sample.NetworkScope,
            sample.CpuPercent is null ? Reason(sample.CpuUnavailableReason ?? sample.Error) ??
                (sample.Interval is null ? "First sample; no CPU baseline." : "CPU counter reset or stalled; new baseline.") : null,
            sample.RamBytes is null ? Reason(sample.Error) ?? "Memory counters unavailable." : null,
            Reason(sample.NetworkUnavailableReason), sample.BytesSentPerSecond is null ?
                Reason(sample.NetworkRateUnavailableReason ?? sample.NetworkUnavailableReason ?? sample.Error) ?? "Network rate baseline unavailable." : null);
    }

    private ResourceMeasurement Read(Func<ResourceCounters> read, ref ResourceCounters? previous, ref long timestamp, bool machine)
    {
        var now = clock.GetTimestamp();
        try
        {
            var current = read();
            var sample = ResourceSampler.Measure(current, previous, clock.GetElapsedTime(timestamp, now), clock.GetUtcNow(), machine);
            previous = current;
            timestamp = now;
            return sample;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            previous = null;
            timestamp = now;
            logger.LogWarning(exception, "Resource counters unavailable for {Scope}.", machine ? "machine" : "task process");
            return new ResourceMeasurement(clock.GetUtcNow(), null, null, null, null, null,
                machine ? "Machine counters unavailable." : ResourceSampler.ProcessNetworkUnavailable, Error: exception.Message);
        }
    }

    private sealed class Tracking(ResourceSamplingService owner, Process process, AttemptRef attempt,
        ITaskResourceScope? scope, string? unavailable, Action<AgentMessage>? publish, ulong reservationEpoch) : IDisposable
    {
        public readonly Action<AgentMessage>? Publish = publish;
        public readonly ulong ReservationEpoch = reservationEpoch;
        public ulong Sequence;
        public readonly Process Process = process;
        public readonly AttemptRef Attempt = attempt;
        public ITaskResourceScope? Scope = scope;
        public string? NativeUnavailableReason = unavailable;
        public DateTimeOffset? StartedAt;
        public ResourceCounters? Counters;
        public long Timestamp;
        public TaskResourceSample? Latest;

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (!owner.tasks.Remove(this)) return;
            }
            try { Scope?.EndAttempt(); }
            catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
            {
                NativeUnavailableReason = exception.Message;
            }
            lock (owner.gate) owner.SampleTask(this);
        }
    }
}
