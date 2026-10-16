using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wf.Contracts.Agent.V1;

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

    public IDisposable Track(Process process, AttemptRef attempt)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(attempt);
        lock (gate)
        {
            var tracking = new Tracking(this, process, attempt.Clone());
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
            tracking.Process.Refresh();
            if (tracking.Process.HasExited) throw new InvalidOperationException("Task process has exited.");
            var started = new DateTimeOffset(tracking.Process.StartTime.ToUniversalTime());
            if (tracking.StartedAt is { } expected && expected != started)
                throw new InvalidOperationException("Task process identity changed; refusing a reused PID.");
            tracking.StartedAt = started;
            return sampler.ReadProcess(tracking.Process);
        }, ref tracking.Counters, ref tracking.Timestamp, machine: false);
        var attempt = tracking.Attempt;
        tracking.Latest = new TaskResourceSample(attempt.RunId, attempt.NodeKey, attempt.TaskKey, attempt.Attempt,
            tracking.Process.Id, tracking.StartedAt, sample);
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

    private sealed class Tracking(ResourceSamplingService owner, Process process, AttemptRef attempt) : IDisposable
    {
        public readonly Process Process = process;
        public readonly AttemptRef Attempt = attempt;
        public DateTimeOffset? StartedAt;
        public ResourceCounters? Counters;
        public long Timestamp;
        public TaskResourceSample? Latest;

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (!owner.tasks.Remove(this)) return;
                owner.SampleTask(this);
            }
        }
    }
}
