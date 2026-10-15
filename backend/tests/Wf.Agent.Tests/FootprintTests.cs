using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Contracts.Agent.V1;
using Wf.TaskHost.Fixture;

namespace Wf.Agent.Tests;

public sealed class FootprintTests(FootprintLauncher launcher) : IClassFixture<FootprintLauncher>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private TaskHostPackage Package(string version = "1.0.0", params string[] arguments) => new(version, "dotnet",
        ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(FootprintTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(FootprintTests).Assembly.Location, ".deps.json"),
            launcher.EntryAssembly, "--version", version, .. arguments], AppContext.BaseDirectory);

    private static Dispatch Dispatch(string version = "1.0.0", string task = "echo") => new()
    {
        Version = version,
        Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = task, Attempt = 1 },
        DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
    };

    [Fact]
    public void Default_warm_Rss_cap_is_exactly_200_decimal_MB() =>
        Assert.Equal(200_000_000L, TaskHostPool.DefaultMaxWarmRssBytes);

    [Fact]
    public async Task Default_count_cap_evicts_global_oldest_across_versions_even_with_concurrent_prewarm()
    {
        var clock = new FootprintClock();
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, clock,
            maxWarmRssBytes: 1024L * 1024 * 1024);
        await pool.PrewarmAsync(Package("oldest"), Token);
        var oldest = Assert.Single(pool.WarmProcessIds);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(Enumerable.Range(0, 7).Select(index => pool.PrewarmAsync(Package($"version-{index}"), Token)));
        Assert.Equal(8, pool.ProcessStartCount);
        Assert.Equal(4, pool.WarmProcessIds.Count);
        Assert.DoesNotContain(oldest, pool.WarmProcessIds);
        AssertStopped(oldest);
        Assert.Equal(TaskHostPool.DefaultMaxWarmProcesses, pool.WarmProcessIds.Count);
    }

    [Fact]
    public async Task Rss_cap_is_aggregate_across_versions_and_evicts_oldest()
    {
        const long budget = 90L * 1024 * 1024;
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
            maxWarmProcesses: 10, maxWarmRssBytes: budget);
        await pool.PrewarmAsync(Package("oldest"), Token);
        var oldest = Assert.Single(pool.WarmProcessIds);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(index => pool.PrewarmAsync(Package($"version-{index}"), Token)));
        Assert.NotEmpty(pool.WarmProcessIds);
        Assert.True(pool.WarmProcessIds.Count < 6);
        Assert.True(Rss(pool.WarmProcessIds) <= budget);
        AssertStopped(oldest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(125)]
    public async Task Five_minute_idle_boundary_and_periodic_cleanup_require_no_new_requests(int offsetMilliseconds)
    {
        var clock = new FootprintClock();
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, clock);
        clock.Advance(TimeSpan.FromMilliseconds(offsetMilliseconds));
        await pool.PrewarmAsync(Package(), Token);
        var pid = Assert.Single(pool.WarmProcessIds);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(pid, Assert.Single(pool.WarmProcessIds));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await WaitAsync(() => pool.WarmProcessIds.Count == 0);
        AssertStopped(pid);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(pool.WarmProcessIds);
    }

    [Fact]
    public async Task Expired_host_is_never_dispatched_even_before_a_timer_tick()
    {
        var clock = new FootprintClock();
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, clock,
            warmIdleTimeout: TimeSpan.FromMilliseconds(500));
        await pool.PrewarmAsync(Package(), Token);
        var pid = Assert.Single(pool.WarmProcessIds);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var result = await pool.ExecuteAsync(Package(), Dispatch(), cancellationToken: Token);
        Assert.Equal(Wf.Contracts.Agent.V1.TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, pool.ProcessStartCount);
        AssertStopped(pid);
    }

    [Fact]
    public async Task New_work_does_not_refresh_older_unbound_hosts_and_expiration_preserves_fresher_versions()
    {
        var clock = new FootprintClock();
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, clock);
        await pool.PrewarmAsync(Package("old"), Token);
        var old = Assert.Single(pool.WarmProcessIds);
        clock.Advance(TimeSpan.FromMinutes(2));
        await pool.PrewarmAsync(Package("fresh"), Token);
        var fresh = pool.WarmProcessIds.Single(pid => pid != old);
        clock.Advance(TimeSpan.FromMinutes(3));
        await WaitAsync(() => pool.WarmProcessIds.Count == 1);
        Assert.Equal(fresh, Assert.Single(pool.WarmProcessIds));
        AssertStopped(old);
        clock.Advance(TimeSpan.FromMinutes(2));
        await WaitAsync(() => pool.WarmProcessIds.Count == 0);
        AssertStopped(fresh);
    }

    [Fact]
    public async Task Active_attempt_is_excluded_from_warm_caps_and_idle_expiry_without_reuse()
    {
        var clock = new FootprintClock();
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, clock, maxWarmProcesses: 1);
        await pool.PrewarmAsync(Package(), Token);
        var activePid = Assert.Single(pool.WarmProcessIds);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var execution = pool.ExecuteAsync(Package(), Dispatch(task: "hold"), cancellationToken: cancellation.Token);
        Assert.Empty(pool.WarmProcessIds);
        await pool.PrewarmAsync(Package("sibling"), Token);
        var idlePid = Assert.Single(pool.WarmProcessIds);
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitAsync(() => pool.WarmProcessIds.Count == 0);
        AssertStopped(idlePid);
        using (var active = Process.GetProcessById(activePid)) Assert.False(active.HasExited);
        var sibling = await pool.ExecuteAsync(Package(), Dispatch(), cancellationToken: Token);
        Assert.Equal(Wf.Contracts.Agent.V1.TaskOutcome.Succeeded, sibling.Outcome);
        Assert.False(execution.IsCompleted);
        Assert.Equal(3, pool.ProcessStartCount);
        await cancellation.CancelAsync();
        Assert.Equal(Wf.Contracts.Agent.V1.TaskOutcome.Cancelled, (await execution).Outcome);
        AssertStopped(activePid);
    }

    [Fact]
    public async Task Inadmissible_warm_host_is_stopped_and_dispatch_uses_an_independent_cold_process()
    {
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, maxWarmRssBytes: 1);
        await pool.PrewarmAsync(Package(), Token);
        Assert.Empty(pool.WarmProcessIds);
        var result = await pool.ExecuteAsync(Package(), Dispatch(), cancellationToken: Token);
        Assert.Equal(Wf.Contracts.Agent.V1.TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, pool.ProcessStartCount);
        Assert.Empty(pool.WarmProcessIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Periodic_sampling_catches_Rss_growth_without_work_and_during_handshake(bool handshaking)
    {
        using var files = new FootprintFiles();
        var trigger = Path.Combine(files.Root, "grow");
        var marker = Path.Combine(files.Root, "started");
        var clock = new FootprintClock();
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, clock,
            maxWarmRssBytes: 100L * 1024 * 1024);
        var arguments = new List<string> { "--grow-trigger", trigger, "--grow-mb", "140", "--started-marker", marker };
        if (handshaking) arguments.AddRange(["--ready-delay-ms", "5000"]);
        var warming = pool.PrewarmAsync(Package("grow", arguments.ToArray()), Token);
        await WaitAsync(() => File.Exists(marker));
        if (!handshaking) await warming;
        // The fixture grows after the marker is written, whether Ready has completed or not.
        await File.WriteAllTextAsync(trigger, "go", Token);
        await WaitAsync(() => File.Exists(trigger + ".grown") || (handshaking && warming.IsCompleted));
        clock.Advance(TimeSpan.FromSeconds(1));
        await warming.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await WaitAsync(() => pool.WarmProcessIds.Count == 0);
        AssertStopped(await ReadPidAsync(marker));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_warm_host_Rss_growth_is_sampled_while_another_version_is_starting(bool prewarming)
    {
        using var files = new FootprintFiles();
        var trigger = Path.Combine(files.Root, "grow");
        var marker = Path.Combine(files.Root, "starting");
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, maxWarmRssBytes: 100L * 1024 * 1024);
        await pool.PrewarmAsync(Package("old", "--grow-trigger", trigger, "--grow-mb", "140"), Token);
        var oldest = Assert.Single(pool.WarmProcessIds);
        var package = Package("fresh", "--ready-delay-ms", "2200", "--started-marker", marker);
        var execution = prewarming ? null : pool.ExecuteAsync(package, Dispatch("fresh"), cancellationToken: Token);
        Task warming = execution ?? (Task)pool.PrewarmAsync(package, Token);
        await WaitAsync(() => File.Exists(marker));
        await File.WriteAllTextAsync(trigger, "go", Token);
        await warming;
        AssertStopped(oldest);
        if (prewarming) Assert.Single(pool.WarmProcessIds);
        else Assert.Empty(pool.WarmProcessIds);
        var result = execution is not null ? await execution : await pool.ExecuteAsync(package, Dispatch("fresh"), cancellationToken: Token);
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, pool.ProcessStartCount);
    }

    [Fact]
    public async Task Cancelled_queued_prewarm_does_not_evict_existing_hosts_or_consume_a_start()
    {
        using var files = new FootprintFiles();
        var marker = Path.Combine(files.Root, "starting");
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, maxWarmProcesses: 2);
        await pool.PrewarmAsync(Package("existing"), Token);
        var existing = Assert.Single(pool.WarmProcessIds);
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var warming = pool.PrewarmAsync(Package("slow", "--ready-delay-ms", "10000", "--started-marker", marker), starting.Token);
        await WaitAsync(() => File.Exists(marker));
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = pool.PrewarmAsync(Package("queued"), queued.Token);
        await queued.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.PrewarmAsync(Package("pre-cancelled"), queued.Token));
        await starting.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
        Assert.Equal(existing, Assert.Single(pool.WarmProcessIds));
        Assert.Equal(2, pool.ProcessStartCount);
    }

    [Fact]
    public async Task Cancellation_and_disposal_interrupt_startup_and_leave_no_hosts()
    {
        using var files = new FootprintFiles();
        var marker = Path.Combine(files.Root, "started");
        var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var warming = pool.PrewarmAsync(Package("slow", "--ready-delay-ms", "10000", "--started-marker", marker), cancellation.Token);
            await WaitAsync(() => File.Exists(marker));
            var pid = await ReadPidAsync(marker);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
            Assert.Empty(pool.WarmProcessIds);
            AssertStopped(pid);
            File.Delete(marker);
            warming = pool.PrewarmAsync(Package("slow", "--ready-delay-ms", "10000", "--started-marker", marker), Token);
            await WaitAsync(() => File.Exists(marker));
            pid = await ReadPidAsync(marker);
            await pool.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
            AssertStopped(pid);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.PrewarmAsync(Package(), Token));
        }
        finally { await pool.DisposeAsync(); }
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("count")]
    [InlineData("rss")]
    public async Task Eviction_releases_package_cache_pins_only_after_real_process_exit(string reason)
    {
        var a = await ArchiveAsync("a");
        var b = await ArchiveAsync("b");
        using var cache = new PackageCacheLruTests.BoundedCacheFixture([a, b], maxPackages: 1);
        var clock = new FootprintClock();
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, clock,
            maxWarmProcesses: reason == "count" ? 1 : 4, maxWarmRssBytes: reason == "rss" ? 70L * 1024 * 1024 : TaskHostPool.DefaultMaxWarmRssBytes);
        using (var package = await cache.GetAsync(a, version: "1.0.0")) await pool.PrewarmAsync(package, Token);
        var pid = Assert.Single(pool.WarmProcessIds);
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(b, version: "1.0.0"));
        if (reason == "idle") clock.Advance(TimeSpan.FromMinutes(5));
        else await pool.PrewarmAsync(Package("another-version"), Token);
        await WaitAsync(() => !pool.WarmProcessIds.Contains(pid));
        AssertStopped(pid);
        using var replacement = await cache.GetAsync(b, version: "1.0.0");
        Assert.False(Directory.Exists(cache.PathFor(a)));
    }

    [Fact]
    public async Task Pool_disposal_stops_an_active_process_before_releasing_its_package_cache_pin()
    {
        var a = await ArchiveAsync("a");
        var b = await ArchiveAsync("b");
        using var cache = new PackageCacheLruTests.BoundedCacheFixture([a, b], maxPackages: 1);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using var package = await cache.GetAsync(a, version: "1.0.0");
        await pool.PrewarmAsync(package, Token);
        var pid = Assert.Single(pool.WarmProcessIds);
        var execution = pool.ExecuteAsync(package, Dispatch(task: "delay"), cancellationToken: Token);
        package.Dispose();
        Assert.Empty(pool.WarmProcessIds);
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(b, version: "1.0.0"));
        await pool.DisposeAsync();
        AssertStopped(pid);
        try { await execution; }
        catch (Grpc.Core.RpcException) { }
        using var replacement = await cache.GetAsync(b, version: "1.0.0");
        Assert.False(Directory.Exists(cache.PathFor(a)));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, 0)]
    public void Invalid_warm_budgets_fail_before_starting_background_maintenance(int count, long bytes, int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskHostPool(NullLogger<TaskHostPool>.Instance,
            maxWarmProcesses: count, maxWarmRssBytes: bytes, warmIdleTimeout: TimeSpan.FromSeconds(seconds)));
    }

    private static long Rss(IEnumerable<int> pids) => pids.Sum(pid =>
    {
        using var process = Process.GetProcessById(pid);
        process.Refresh();
        return process.WorkingSet64;
    });

    private async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private async Task<int> ReadPidAsync(string marker)
    {
        var pid = 0;
        await WaitAsync(() =>
        {
            try { return int.TryParse(File.ReadAllText(marker), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out pid); }
            catch (IOException) { return false; }
        });
        return pid;
    }

    private static bool Stopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static void AssertStopped(int pid) => Assert.True(Stopped(pid), $"Host {pid} is still running.");

    private async Task<byte[]> ArchiveAsync(string marker)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                await using var source = File.OpenRead(path);
                await using var destination = archive.CreateEntry(Path.GetFileName(path)).Open();
                await source.CopyToAsync(destination, Token);
            }
            await using (var writer = new StreamWriter(archive.CreateEntry("wf-package.json").Open(), Encoding.UTF8))
                await writer.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""");
            await using (var writer = new StreamWriter(archive.CreateEntry("marker.txt").Open(), Encoding.UTF8))
                await writer.WriteAsync(marker);
        }
        return memory.ToArray();
    }
}

internal sealed class FootprintFiles : IDisposable
{
    public string Root { get; } = Path.GetFullPath(Path.Combine(".agent-artifacts", "footprint", Guid.NewGuid().ToString("N")));
    public FootprintFiles() => Directory.CreateDirectory(Root);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class FootprintClock : TimeProvider
{
    private readonly object sync = new();
    private readonly HashSet<ClockTimer> timers = [];
    private DateTimeOffset now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (sync) return now.UtcTicks; }
    public override DateTimeOffset GetUtcNow() { lock (sync) return now; }

    public void Advance(TimeSpan elapsed)
    {
        List<ClockTimer> due;
        lock (sync)
        {
            now += elapsed;
            due = timers.Where(timer => timer.At <= now).ToList();
            foreach (var timer in due)
                timer.At = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue :
                    timer.At + TimeSpan.FromTicks(((now - timer.At).Ticks / timer.Period.Ticks + 1) * timer.Period.Ticks);
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ClockTimer(FootprintClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset At { get; set; }
        public TimeSpan Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync)
            {
                At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
                Period = period;
                owner.timers.Add(this);
                return true;
            }
        }
        public void Dispose() { lock (owner.sync) owner.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
