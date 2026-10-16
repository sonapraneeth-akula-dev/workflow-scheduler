using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Agent.Sampling;
using Wf.Contracts.Agent.V1;
using Wf.TaskHost.Fixture;

namespace Wf.Agent.Tests;

public sealed class ResourceSamplerTests
{
    [Fact]
    public void Native_machine_and_process_counters_are_real_and_process_network_is_unavailable()
    {
        var reader = ResourceSampler.Create();
        var first = reader.ReadMachine();
        using var process = Process.GetCurrentProcess();
        var taskFirst = reader.ReadProcess(process);
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(150)) _ = Math.Sqrt(Random.Shared.NextDouble());
        var second = reader.ReadMachine();
        var taskSecond = reader.ReadProcess(process);
        Assert.True(first.RamBytes > 0);
        Assert.True(second.CpuTotal > first.CpuTotal);
        Assert.InRange(second.CpuBusy - first.CpuBusy, 0UL, second.CpuTotal - first.CpuTotal);
        Assert.True(taskFirst.RamBytes > 0);
        Assert.True(taskSecond.CpuBusy > taskFirst.CpuBusy);
        Assert.Null(taskSecond.Network);
        Assert.Contains("per-process", taskSecond.NetworkUnavailableReason);
        Assert.NotNull(second.Network);
        Assert.NotEmpty(second.Network);
    }

    [Fact]
    public void Platform_selection_and_linux_proc_cpu_scope_are_correct()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxResourceSampler>(ResourceSampler.Create());
            Assert.Throws<PlatformNotSupportedException>(() => new WindowsResourceSampler().ReadMachine());
            var before = ProcCpuTotal();
            var current = new LinuxResourceSampler().ReadMachine();
            var after = ProcCpuTotal();
            Assert.InRange(current.CpuTotal, before, after);
        }
        else
        {
            Assert.IsType<WindowsResourceSampler>(ResourceSampler.Create());
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxResourceSampler().ReadMachine());
        }

        static ulong ProcCpuTotal() => File.ReadLines("/proc/stat").First()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Aggregate(0UL, (sum, value) => sum + ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Delta_math_handles_first_samples_cpu_cores_counter_resets_and_interface_changes()
    {
        var first = new ResourceCounters(100, 1000, 4096, new Dictionary<string, NetworkBytes> { ["eth0"] = new(100, 200) });
        var second = new ResourceCounters(500, 1200, 8192, new Dictionary<string, NetworkBytes> { ["eth0"] = new(300, 600) });
        var initial = ResourceSampler.Measure(first, null, TimeSpan.Zero, DateTimeOffset.UtcNow, machine: false);
        Assert.Null(initial.CpuPercent);
        Assert.Null(initial.BytesSentPerSecond);
        Assert.Null(initial.NetworkDelta);
        Assert.Contains("First", initial.NetworkRateUnavailableReason);
        var sample = ResourceSampler.Measure(second, first, TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow, machine: false);
        Assert.Equal(200, sample.CpuPercent);
        Assert.Equal(100, sample.BytesSentPerSecond);
        Assert.Equal(200, sample.BytesReceivedPerSecond);
        Assert.Equal(8192UL, sample.RamBytes);
        Assert.Equal(300UL, sample.Network!.Sent);
        Assert.Equal(new NetworkBytes(200, 400), sample.NetworkDelta);
        Assert.Null(sample.NetworkRateUnavailableReason);
        Assert.Null(ResourceSampler.Measure(first, second, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, false).CpuPercent);
        Assert.Null(ResourceSampler.Measure(first, second, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, false).BytesSentPerSecond);
        Assert.Null(ResourceSampler.Measure(first, second, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, false).NetworkDelta);
        var changed = second with { Network = new Dictionary<string, NetworkBytes> { ["eth1"] = new(300, 600) } };
        Assert.Null(ResourceSampler.Measure(changed, first, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, false).BytesSentPerSecond);
        Assert.Null(ResourceSampler.Measure(second, first, TimeSpan.Zero, DateTimeOffset.UtcNow, false).BytesSentPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5001)]
    public void Invalid_sampling_intervals_are_rejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceSamplingService(
            ResourceSampler.Create(), TimeProvider.System, NullLogger<ResourceSamplingService>.Instance,
            TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task Hosted_loop_samples_real_machine_and_actual_bound_task_host_and_unregisters_after_cancellation()
    {
        using var service = new ResourceSamplingService(ResourceSampler.Create(), TimeProvider.System,
            NullLogger<ResourceSamplingService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, sampling: service);
        var package = new TaskHostPackage("1.0.0", "dotnet",
            ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(ResourceSamplerTests).Assembly.Location, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(typeof(ResourceSamplerTests).Assembly.Location, ".deps.json"), typeof(FixtureMarker).Assembly.Location],
            AppContext.BaseDirectory);
        await pool.PrewarmAsync(package, TestContext.Current.CancellationToken);
        Assert.Empty(service.ActiveTasks);
        var pid = Assert.Single(pool.WarmProcessIds);
        var dispatch = new Dispatch
        {
            Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "delay", Attempt = 1 },
            Version = "1.0.0", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30)),
            InputsJson = ByteString.CopyFromUtf8("""{"source":{"value":7}}""")
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var execution = pool.ExecuteAsync(package, dispatch, cancellationToken: cancellation.Token);
        try
        {
            var first = Assert.Single(service.ActiveTasks);
            Assert.Equal(pid, first.ProcessId);
            Assert.Equal(dispatch.Task.RunId, first.RunId);
            Assert.True(first.Sample.RamBytes > 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            TaskResourceSample current;
            do
            {
                await Task.Delay(25, timeout.Token);
                current = Assert.Single(service.ActiveTasks);
            } while (current.Sample.At == first.Sample.At);
            Assert.NotNull(current.Sample.CpuPercent);
            Assert.InRange(current.Sample.At - first.Sample.At, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            Assert.Null(current.Sample.Network);
            Assert.Contains("per-process", current.Sample.NetworkUnavailableReason);
            Assert.Null(current.Sample.Error);
            Assert.NotNull(service.LatestMachine);
            if (service.LatestMachine.CpuUnavailableReason is null) Assert.NotNull(service.LatestMachine.CpuPercent);
            else Assert.Null(service.LatestMachine.CpuPercent);
            Assert.NotNull(service.LatestMachine.Network);
            Assert.Null(service.LatestMachine.Error);
            var sibling = dispatch.Clone();
            sibling.Task.RunId = Guid.NewGuid().ToString();
            sibling.Task.TaskKey = "echo";
            var result = await pool.ExecuteAsync(package, sibling, ByteString.CopyFromUtf8("""{"previous":3}"""),
                cancellationToken: TestContext.Current.CancellationToken, journalResult: _ =>
                {
                    Assert.Equal(pid, Assert.Single(service.ActiveTasks).ProcessId);
                });
            Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
            Assert.Equal(pid, Assert.Single(service.ActiveTasks).ProcessId);
        }
        finally
        {
            await cancellation.CancelAsync();
            await execution;
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
        Assert.Empty(service.ActiveTasks);
    }

    [Fact]
    public async Task Exited_process_reports_unavailable_without_stale_or_fabricated_values()
    {
        var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("/bin/sh");
        info.UseShellExecute = false;
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        info.ArgumentList.Add("exit 0");
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        using var service = new ResourceSamplingService(ResourceSampler.Create(), TimeProvider.System,
            NullLogger<ResourceSamplingService>.Instance);
        using var tracking = service.Track(process, new AttemptRef { RunId = "run", NodeKey = "node", TaskKey = "task", Attempt = 1 });
        var sample = Assert.Single(service.ActiveTasks).Sample;
        Assert.NotNull(sample.Error);
        Assert.Null(sample.CpuPercent);
        Assert.Null(sample.RamBytes);
        Assert.Null(sample.Network);
    }

    [Fact]
    public async Task Machine_read_errors_are_explicit_and_hosted_sampling_recovers()
    {
        var reader = new RecoveringSampler();
        using var service = new ResourceSamplingService(reader, TimeProvider.System,
            NullLogger<ResourceSamplingService>.Instance, TimeSpan.FromMilliseconds(50));
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (service.LatestMachine is null) await Task.Delay(5, timeout.Token);
            var failed = service.LatestMachine;
            Assert.NotNull(failed.Error);
            Assert.Null(failed.RamBytes);
            Assert.Null(failed.CpuPercent);
            reader.Fail = false;
            while (service.LatestMachine is not { CpuPercent: not null, Error: null }) await Task.Delay(5, timeout.Token);
            Assert.Equal(10, service.LatestMachine.CpuPercent);
        }
        finally { await service.StopAsync(TestContext.Current.CancellationToken); }
    }

    private sealed class RecoveringSampler : ResourceSampler
    {
        public volatile bool Fail = true;
        private ulong tick;
        public override ResourceCounters ReadMachine()
        {
            if (Fail) throw new IOException("scripted unavailable counters");
            tick++;
            return new ResourceCounters(tick * 10, tick * 100, 4096, null, "scripted unavailable network");
        }
    }
}
