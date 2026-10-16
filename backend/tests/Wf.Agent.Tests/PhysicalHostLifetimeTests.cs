using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Contracts.Agent.V1;
using Wf.TaskHost.Fixture;

namespace Wf.Agent.Tests;

public sealed class PhysicalHostLifetimeTests
{
    internal static TaskHostPackage Package => new("1.0.0", "dotnet",
        ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(PhysicalHostLifetimeTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(PhysicalHostLifetimeTests).Assembly.Location, ".deps.json"),
            typeof(FixtureMarker).Assembly.Location, "--physical-lifetime-fixture"], AppContext.BaseDirectory);

    internal static Dispatch Dispatch(string task, string marker) => new()
    {
        Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = task, Attempt = 1 },
        Version = "1.0.0", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
        VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { marker }))
    };

    internal static string Root()
    {
        var path = Path.GetFullPath(Path.Combine(".agent-artifacts", "physical-fence-3adec91f", "cases", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static TaskHostPool Pool(string root) => new(NullLogger<TaskHostPool>.Instance,
        containmentDirectory: Path.Combine(root, "recovery"));

    internal static AgentOptions Options(string root, string id = "2b14cc04-4eac-49e3-b23b-904de0cd158a") =>
        new([new Uri("http://localhost")], id, "physical-fixture", "default", 1,
            resultJournalDirectory: Path.Combine(root, "journal"));

    [Theory]
    [InlineData("cooperate")]
    [InlineData("block")]
    [InlineData("complete")]
    [InlineData("exit")]
    public async Task Actual_sdk_host_cleans_detached_descendants_before_execution_or_fence_completes(string task)
    {
        var root = Root();
        var marker = Path.Combine(root, "child.pid");
        using var unrelated = StartUnrelated();
        await using var pool = Pool(root);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var execution = pool.ExecuteAsync(Package, Dispatch(task, marker), cancellationGrace: TimeSpan.FromMilliseconds(100),
                cancellationToken: cancellation.Token);
            var child = await ReadPid(marker);
            if (task is "cooperate" or "block") await cancellation.CancelAsync();
            if (task == "exit") await Assert.ThrowsAnyAsync<Exception>(() => execution);
            else
            {
                var result = await execution;
                Assert.Equal(task == "complete" ? TaskOutcome.Succeeded : TaskOutcome.Cancelled, result.Outcome);
                if (task == "block") Assert.Contains("terminated", result.Error.Message);
            }
            await pool.FenceAsync(TestContext.Current.CancellationToken);
            await AssertStopped(child);
            Assert.False(unrelated.HasExited);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "recovery"), "*.pending"));
        }
        finally
        {
            StopUnrelated(unrelated);
            await pool.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fence_physically_terminates_warm_hosts_without_touching_unrelated_process()
    {
        var root = Root();
        using var unrelated = StartUnrelated();
        await using var pool = Pool(root);
        try
        {
            await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
            var pid = Assert.Single(pool.WarmProcessIds);
            await pool.FenceAsync(TestContext.Current.CancellationToken);
            await AssertStopped(pid);
            Assert.Empty(pool.WarmProcessIds);
            Assert.False(unrelated.HasExited);
        }
        finally { StopUnrelated(unrelated); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Physical_fence_does_not_wait_for_uncooperative_execution_or_its_thirty_second_cancel_grace()
    {
        var root = Root();
        var marker = Path.Combine(root, "child.pid");
        await using var pool = Pool(root);
        try
        {
            var execution = pool.ExecuteAsync(Package, Dispatch("block", marker),
                cancellationToken: TestContext.Current.CancellationToken);
            var child = await ReadPid(marker);
            await pool.FenceAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(() => execution);
            await AssertStopped(child);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "recovery"), "*.pending"));
        }
        finally { await pool.DisposeAsync(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Fence_cannot_ignore_another_pools_live_physical_host()
    {
        var root = Root();
        await using var first = Pool(root);
        await using var second = Pool(root);
        try
        {
            await first.PrewarmAsync(Package, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.FenceAsync(TestContext.Current.CancellationToken));
            await first.FenceAsync(TestContext.Current.CancellationToken);
            await second.FenceAsync(TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unexpected_supervisor_death_never_certifies_quiescence_or_allows_reuse()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Root();
        var marker = Path.Combine(root, "child.pid");
        var pool = Pool(root);
        var child = 0;
        var worker = 0;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
            using var supervisor = Process.GetProcessById(Assert.Single(pool.WarmProcessIds));
            var execution = pool.ExecuteAsync(Package, Dispatch("block", marker), cancellationGrace: TimeSpan.Zero,
                cancellationToken: cancellation.Token);
            child = await ReadPid(marker);
            worker = await ReadPid(marker + ".worker");
            supervisor.Kill(entireProcessTree: false);
            await supervisor.WaitForExitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => execution);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.FenceAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.PrewarmAsync(Package, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "recovery"), "*.pending"));
        }
        finally
        {
            // The deliberately destroyed supervisor cannot clean its child; fixture owns this exact PID.
            foreach (var pid in new[] { worker, child }.Where(pid => pid != 0))
            {
                try
                {
                    using var owned = Process.GetProcessById(pid);
                    if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
            await Assert.ThrowsAnyAsync<Exception>(async () => await pool.DisposeAsync());
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_parent_SIGKILL_stops_owned_hosts_and_descendants_but_restart_quarantines_until_reboot(bool productionIdentity)
    {
        var root = Root();
        using var unrelated = StartUnrelated();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var value in new[] { typeof(PhysicalHostLifetimeTests).Assembly.Location, "-class",
            "Wf.Agent.Tests.PhysicalHostCrashDriver", "-parallel", "none" }) start.ArgumentList.Add(value);
        start.Environment["WF_PHYSICAL_CRASH_ROOT"] = root;
        start.Environment["WF_PHYSICAL_PRODUCTION_IDENTITY"] = productionIdentity.ToString(CultureInfo.InvariantCulture);
        using var parent = Process.Start(start)!;
        try
        {
            var child = await ReadPid(Path.Combine(root, "child.pid"));
            var worker = await ReadPid(Path.Combine(root, "child.pid.worker"));
            var host = await ReadPid(Path.Combine(root, "host.pid"));
            parent.Kill(entireProcessTree: false);
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken);
            await AssertStopped(host);
            await AssertStopped(worker);
            await AssertStopped(child);
            Assert.False(unrelated.HasExited);
            await using var restarted = productionIdentity
                ? new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: Options(root))
                : Pool(root);
            var fence = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.FenceAsync(TestContext.Current.CancellationToken));
            Assert.Contains("Reboot this machine", fence.Message);
            var admission = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrewarmAsync(Package,
                TestContext.Current.CancellationToken));
            Assert.Contains("quarantined", admission.Message);
            var recovery = productionIdentity
                ? Path.Combine(Options(root).ResultJournalDirectory, "host-lifetimes", Guid.Parse(Options(root).Id).ToString("N"))
                : Path.Combine(root, "recovery");
            Assert.Single(Directory.GetFiles(recovery, "*.pending"));
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(TestContext.Current.CancellationToken); }
            StopUnrelated(unrelated);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Prior_boot_barrier_can_recover_but_malformed_or_same_boot_cannot_be_silently_discarded()
    {
        var root = Root();
        try
        {
            var recovery = Path.Combine(root, "recovery");
            Directory.CreateDirectory(recovery);
            var path = Path.Combine(recovery, "old.pending");
            await File.WriteAllTextAsync(path, Guid.NewGuid().ToString("D"), TestContext.Current.CancellationToken);
            await using var pool = Pool(root);
            await pool.FenceAsync(TestContext.Current.CancellationToken);
            Assert.False(File.Exists(path));
            await File.WriteAllTextAsync(path, "invalid", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => pool.FenceAsync(TestContext.Current.CancellationToken));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Production_agent_identity_isolates_unrelated_agents_but_same_identity_cannot_ignore_a_live_host()
    {
        var root = Root();
        await using var services = new ServiceCollection()
            .AddSingleton<ILogger<TaskHostPool>>(NullLogger<TaskHostPool>.Instance)
            .AddSingleton(Options(root)).AddSingleton<TaskHostPool>().BuildServiceProvider();
        var first = services.GetRequiredService<TaskHostPool>();
        await using var same = new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: Options(root));
        await using var unrelated = new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: Options(root, Guid.NewGuid().ToString()));
        try
        {
            await first.PrewarmAsync(Package, TestContext.Current.CancellationToken);
            await unrelated.FenceAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => same.FenceAsync(TestContext.Current.CancellationToken));
            await first.FenceAsync(TestContext.Current.CancellationToken);
            await same.FenceAsync(TestContext.Current.CancellationToken);
        }
        finally { await first.DisposeAsync(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cached_protocol_two_host_is_rejected_and_zero_exit_without_supervisor_receipt_is_not_trusted()
    {
        var root = Root();
        var arguments = Package.Arguments.ToArray();
        arguments[^1] = "--physical-protocol2-fixture";
        var legacy = new TaskHostPackage(Package.Version, Package.Executable, arguments, Package.WorkingDirectory);
        var pool = Pool(root);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => pool.PrewarmAsync(legacy, TestContext.Current.CancellationToken));
            if (OperatingSystem.IsLinux())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => pool.FenceAsync(TestContext.Current.CancellationToken));
                Assert.Single(Directory.GetFiles(Path.Combine(root, "recovery"), "*.pending"));
            }
            else await pool.FenceAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (OperatingSystem.IsLinux())
                await Assert.ThrowsAnyAsync<Exception>(async () => await pool.DisposeAsync());
            else await pool.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Supervisor_reexecution_preserves_empty_package_arguments()
    {
        var root = Root();
        var arguments = Package.Arguments.ToArray();
        arguments[^1] = "--physical-empty-argument-fixture";
        var package = new TaskHostPackage(Package.Version, Package.Executable, [.. arguments, ""], Package.WorkingDirectory);
        await using var pool = Pool(root);
        try
        {
            var result = await pool.ExecuteAsync(package, Dispatch("complete", Path.Combine(root, "child.pid")),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
            await pool.FenceAsync(TestContext.Current.CancellationToken);
        }
        finally { await pool.DisposeAsync(); Directory.Delete(root, recursive: true); }
    }

    internal static async Task<int> ReadPid(string marker)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                if (int.TryParse(await File.ReadAllTextAsync(marker, timeout.Token), CultureInfo.InvariantCulture, out var pid))
                    return pid;
            }
            catch (IOException) { }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task AssertStopped(int pid)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return;
                if (OperatingSystem.IsLinux() && File.ReadAllText($"/proc/{pid}/stat").Split(')')[1].TrimStart().StartsWith('Z'))
                    return;
            }
            catch (ArgumentException) { return; }
            catch (FileNotFoundException) { return; }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static Process StartUnrelated()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") { UseShellExecute = false };
        foreach (var value in OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", "ping -n 120 127.0.0.1 >nul" }
            : new[] { "-c", "sleep 120" }) start.ArgumentList.Add(value);
        return Process.Start(start)!;
    }

    private static void StopUnrelated(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }
}

public sealed class PhysicalHostCrashDriver
{
    [Fact]
    public async Task Agent_style_parent()
    {
        if (Environment.GetEnvironmentVariable("WF_PHYSICAL_CRASH_ROOT") is not { } root) return;
        await using var pool = Environment.GetEnvironmentVariable("WF_PHYSICAL_PRODUCTION_IDENTITY") == "True"
            ? new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: PhysicalHostLifetimeTests.Options(root))
            : PhysicalHostLifetimeTests.Pool(root);
        await pool.PrewarmAsync(PhysicalHostLifetimeTests.Package, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "host.pid"), Assert.Single(pool.WarmProcessIds).ToString(CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken);
        await pool.ExecuteAsync(PhysicalHostLifetimeTests.Package,
            PhysicalHostLifetimeTests.Dispatch("block", Path.Combine(root, "child.pid")),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
