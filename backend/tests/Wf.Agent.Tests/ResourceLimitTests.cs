using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Agent.Limits;
using Wf.Agent.Sampling;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Resources;
using Wf.TaskHost.Fixture;

namespace Wf.Agent.Tests;

public sealed class ResourceLimitTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(".agent-artifacts", "limits", Guid.NewGuid().ToString("N")));
    private static string RuntimeConfig => Path.ChangeExtension(typeof(ResourceLimitTests).Assembly.Location, ".runtimeconfig.json");
    private static string DepsFile => Path.ChangeExtension(typeof(ResourceLimitTests).Assembly.Location, ".deps.json");
    private static TaskHostPackage Package => new("1.0.0", "dotnet",
        ["exec", "--runtimeconfig", RuntimeConfig, "--depsfile", DepsFile,
            typeof(FixtureMarker).Assembly.Location, "--resource-limit-fixture"], AppContext.BaseDirectory);
    private TaskHostPool Pool(string? delegated = null) => new(NullLogger<TaskHostPool>.Instance,
        maxWarmRssBytes: 1_000_000_000, containmentDirectory: Path.Combine(root, Guid.NewGuid().ToString("N")),
        options: new AgentOptions([new Uri("http://localhost")], Guid.NewGuid().ToString(), "limits", "test", 4,
            linuxCgroupRoot: delegated));

    [Theory]
    [InlineData("memory")]
    [InlineData("caughtMemory")]
    [InlineData("childMemory")]
    public async Task Actual_RAM_breach_including_descendants_and_caught_OOM_fails_nonretryably_after_physical_exit(string task)
    {
        var delegated = Delegation();
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, task);
        await using var pool = Pool(delegated);
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var supervisor = Assert.Single(pool.WarmProcessIds);
        var dispatch = Request(task, marker, new() { RamMb = 192 });
        var called = false;
        var result = await pool.ExecuteAsync(Package, dispatch, cancellationToken: TestContext.Current.CancellationToken,
            journalResult: completion =>
            {
                Assert.Equal("ResourceLimit", completion.Error.Category);
                AssertStopped(supervisor);
                AssertWorkloadStopped(marker);
                called = true;
            });
        Assert.True(called);
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal("ResourceLimit", result.Error.Category);
        Assert.True(result.Error.NonRetryable);
        Assert.True(result.OutputsJson.IsEmpty);
        Assert.Empty(pool.WarmProcessIds);
        if (delegated is not null)
            Assert.Empty(Directory.GetDirectories(delegated, "wf-limits-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_CPU_hard_cap_throttles_aggregate_work_without_failing_task(bool child)
    {
        var delegated = Delegation();
        Directory.CreateDirectory(root);
        await using var pool = Pool(delegated);
        var result = await pool.ExecuteAsync(Package, Request(child ? "childCpu" : "cpu", Path.Combine(root, "cpu"),
            new() { CpuCores = 0.25 }), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        using var outputs = JsonDocument.Parse(result.OutputsJson.Memory);
        var cpu = outputs.RootElement.GetProperty("cpuSeconds").GetDouble();
        var wall = outputs.RootElement.GetProperty("elapsedSeconds").GetDouble();
        Assert.True(cpu > 0.1, $"Workload did not consume meaningful CPU: {cpu:F3}s.");
        Assert.True(cpu / wall <= 0.36, $"Aggregate cap exceeded: {cpu:F3}s CPU / {wall:F3}s wall.");
        AssertWorkloadStopped(Path.Combine(root, "cpu"));
    }

    [Fact]
    public async Task RAM_cap_below_warm_worker_baseline_is_a_resource_failure_before_delegate()
    {
        var delegated = Delegation();
        Directory.CreateDirectory(root);
        await using var pool = Pool(delegated);
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var supervisor = Assert.Single(pool.WarmProcessIds);
        if (delegated is not null)
        {
            var cgroup = Assert.Single(Directory.GetDirectories(delegated, "wf-limits-*"));
            Assert.True(ulong.Parse(File.ReadAllText(Path.Combine(cgroup, "memory.current")),
                System.Globalization.CultureInfo.InvariantCulture) > 1024 * 1024,
                "Warm worker baseline must already be charged to the future limited cgroup.");
        }
        var result = await pool.ExecuteAsync(Package,
            Request("echo", Path.Combine(root, "must-not-run"), new() { RamMb = 1 }),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal("ResourceLimit", result.Error.Category);
        Assert.True(result.Error.NonRetryable);
        Assert.True(result.OutputsJson.IsEmpty);
        AssertStopped(supervisor);
    }

    [Fact]
    public async Task Concurrent_limited_breach_does_not_kill_or_poison_another_attempt()
    {
        var delegated = Delegation();
        Directory.CreateDirectory(root);
        await using var pool = Pool(delegated);
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var failed = pool.ExecuteAsync(Package, Request("childMemory", Path.Combine(root, "memory"), new() { RamMb = 192 }),
            cancellationToken: TestContext.Current.CancellationToken);
        var healthy = pool.ExecuteAsync(Package, Request("cpu", Path.Combine(root, "cpu"), new() { CpuCores = 0.25, RamMb = 384 }),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ResourceLimit", (await failed).Error.Category);
        Assert.Equal(TaskOutcome.Succeeded, (await healthy).Outcome);
        AssertWorkloadStopped(Path.Combine(root, "memory"));
        AssertWorkloadStopped(Path.Combine(root, "cpu"));
    }

    [Fact]
    public async Task Native_accounting_includes_limited_worker_but_supervisor_stays_outside_its_RAM_limit()
    {
        var delegated = Delegation();
        Directory.CreateDirectory(root);
        using var reader = ResourceSampler.Create(new NativeSamplingOptions(true, delegated));
        using var service = new ResourceSamplingService(reader, TimeProvider.System, NullLogger<ResourceSamplingService>.Instance);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, sampling: service,
            containmentDirectory: Path.Combine(root, "accounting"));
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var supervisor = Assert.Single(pool.WarmProcessIds);
        var marker = Path.Combine(root, "accounted");
        ResourcePoint? final = null;
        var execution = pool.ExecuteAsync(Package, Request("cpu", marker, new() { CpuCores = 0.25, RamMb = 384 }),
            cancellationToken: TestContext.Current.CancellationToken,
            publishResources: message => final = ResourceContract.Parse(message.ResourceSample.SampleJson.Memory).Task);
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker))
            {
                if (execution.IsCompleted) await execution;
                await Task.Delay(10, wait.Token);
            }
            if (OperatingSystem.IsLinux())
            {
                using var info = JsonDocument.Parse(await File.ReadAllTextAsync(marker, wait.Token));
                var workerPath = info.RootElement.GetProperty("cgroup").GetString()!.Trim().Split("::")[1];
                var supervisorPath = File.ReadAllText($"/proc/{supervisor}/cgroup").Trim().Split("::")[1];
                Assert.EndsWith("/supervisor", supervisorPath);
                Assert.StartsWith(supervisorPath[..^"supervisor".Length], workerPath);
                Assert.Contains("/wf-limits-", workerPath);
                var accounting = Path.Combine(delegated!, supervisorPath.Split('/')[^2]);
                Assert.Empty(File.ReadAllText(Path.Combine(accounting, "cgroup.procs")));
                Assert.Equal("402653184", File.ReadAllText(Path.Combine(accounting, workerPath.Split('/')[^1], "memory.max")).Trim());
            }
        }
        finally { await execution; }
        Assert.Equal(TaskOutcome.Succeeded, (await execution).Outcome);
        var sample = Assert.IsType<ResourcePoint>(final);
        Assert.Null(sample.CpuUnavailableReason);
        Assert.Null(sample.MemoryUnavailableReason);
        Assert.True(sample.RamBytes > 0);
        Assert.Equal(OperatingSystem.IsLinux() ? "cgroup-with-descendants" : "job-with-descendants", sample.CpuScope);
        AssertStopped(supervisor);
        AssertWorkloadStopped(marker);
    }

    [Fact]
    public async Task Requested_Linux_limits_without_delegation_fail_closed_before_delegate()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux delegation failure path.");
        Directory.CreateDirectory(root);
        await using var pool = Pool();
        var marker = Path.Combine(root, "must-not-run");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.ExecuteAsync(Package,
            Request("cpu", marker, new() { CpuCores = 0.25 }), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Agent:LinuxCgroupRoot", error.Message);
        Assert.False(File.Exists(marker));
        Assert.Empty(pool.WarmProcessIds);
    }

    [Fact]
    public void Cgroup_limit_files_cannot_be_faked_with_an_ordinary_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux cgroup filesystem validation.");
        Directory.CreateDirectory(root);
        var error = Assert.Throws<InvalidOperationException>(() => new CgroupV2Limiter(root, new(cpuCores: 0.25)));
        Assert.Contains("real cgroup-v2", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData(double.NaN, 0UL)]
    [InlineData(double.PositiveInfinity, 0UL)]
    [InlineData(-1, 0UL)]
    [InlineData(0.001, 0UL)]
    [InlineData(0, ulong.MaxValue)]
    public async Task Invalid_wire_caps_are_rejected_before_process_start(double cpu, ulong ram)
    {
        await using var pool = Pool();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pool.ExecuteAsync(Package,
            Request("echo", Path.Combine(root, "unused"), new() { CpuCores = cpu, RamMb = ram }),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, pool.ProcessStartCount);
    }

    private static string? Delegation()
    {
        var path = Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT");
        Assert.SkipWhen(OperatingSystem.IsLinux() && path is null, "Requires isolated delegated cgroup-v2 CPU/memory controllers.");
        return path;
    }

    private static Dispatch Request(string task, string marker, ResourceLimits limits) => new()
    {
        Task = new() { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = task, Attempt = 1 },
        Version = "1.0.0", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30)),
        Limits = limits,
        VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new
        {
            marker, runtimeConfig = RuntimeConfig, depsFile = DepsFile
        }))
    };

    private static void AssertWorkloadStopped(string marker)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(marker));
        AssertStopped(document.RootElement.GetProperty("pid").GetInt32());
        if (File.Exists(marker + ".child"))
            AssertStopped(int.Parse(File.ReadAllText(marker + ".child"), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, $"Owned process {pid} is still alive."); }
        catch (ArgumentException) { }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
