using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using Wf.Agent.ResourceFixture;
using Wf.Agent.Sampling;

namespace Wf.Agent.Tests;

public sealed class WindowsResourceCounterTests(ITestOutputHelper output)
{
    [Fact]
    public void Native_interface_snapshot_preserves_every_nonloopback_interface_and_64bit_byte_counts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var before = Managed();
        using var sampler = new WindowsResourceSampler();
        var actual = sampler.ReadMachine();
        var after = Managed();
        Assert.Null(actual.NetworkUnavailableReason);
        Assert.NotNull(actual.Network);
        Assert.Equal(before.Count, actual.Network.Count);
        Assert.Equal(after.Count, actual.Network.Count);
        Assert.InRange(actual.Network.Values.Aggregate(0UL, (sum, value) => checked(sum + value.Sent)), before.Sent, after.Sent);
        Assert.InRange(actual.Network.Values.Aggregate(0UL, (sum, value) => checked(sum + value.Received)), before.Received, after.Received);
        Assert.Equal("non-loopback-interfaces", actual.NetworkScope);
        Assert.Equal(actual.Network.Keys.Order(), sampler.ReadMachine().Network!.Keys.Order());

        static (int Count, ulong Sent, ulong Received) Managed()
        {
            var stats = NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback).Select(adapter => adapter.GetIPStatistics()).ToArray();
            return (stats.Length, stats.Aggregate(0UL, (sum, value) => checked(sum + (ulong)value.BytesSent)),
                stats.Aggregate(0UL, (sum, value) => checked(sum + (ulong)value.BytesReceived)));
        }
    }

    [Fact]
    public async Task Owned_job_resident_sum_includes_actual_child_and_descendant_working_sets()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sampler = new WindowsResourceSampler(new NativeSamplingOptions(true));
        using var scope = sampler.TryCreateScope(out var reason);
        Assert.NotNull(scope);
        Assert.Null(reason);
        var root = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_ARTIFACTS") ??
            Path.Combine(".agent-artifacts", "native-resource-tests"), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var config = Path.ChangeExtension(typeof(WindowsResourceCounterTests).Assembly.Location, ".runtimeconfig.json");
        var deps = Path.ChangeExtension(typeof(WindowsResourceCounterTests).Assembly.Location, ".deps.json");
        var request = new TrafficRequest(Path.Combine(root, "ready"), Path.Combine(root, "go"),
            Path.Combine(root, "done"), Path.Combine(root, "stop"), true, false, 1, 1024, 1024, true, config, deps);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", config, "--depsfile", deps,
            typeof(ResourceFixtureMarker).Assembly.Location, "--network-worker", JsonSerializer.Serialize(request) })
            info.ArgumentList.Add(argument);
        scope.PrepareStart(info);
        using var child = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            scope.Bind(child);
            scope.BeginAttempt();
            while (!File.Exists(request.Ready)) await Task.Delay(20, timeout.Token);
            var ready = JsonSerializer.Deserialize<TrafficReady>(await File.ReadAllTextAsync(request.Ready, timeout.Token))!;
            Assert.NotEqual(child.Id, ready.ProcessId);
            using var descendant = Process.GetProcessById(ready.ProcessId);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            var before = Resident();
            var sample = scope.ReadCounters();
            var after = Resident();
            Assert.Equal("job-resident-sum", sample.MemoryScope);
            var minimum = Math.Min(before, after);
            Assert.InRange(sample.RamBytes, minimum - Math.Min(minimum, 2 * 1024 * 1024),
                Math.Max(before, after) + 2 * 1024 * 1024);
            Assert.True(sample.RamBytes > (ulong)child.WorkingSet64);
            Assert.True(sample.RamBytes > (ulong)descendant.WorkingSet64);
            scope.EndAttempt();

            ulong Resident()
            {
                // Windows may also assign console hosts to this job: they must not be hidden from its resident sum.
                var ids = (int[])scope.GetType().GetMethod("Members", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!.Invoke(scope, null)!;
                Assert.Contains(child.Id, ids);
                Assert.Contains(descendant.Id, ids);
                ulong total = 0;
                foreach (var pid in ids)
                {
                    using var member = Process.GetProcessById(pid);
                    var resident = checked((ulong)member.WorkingSet64);
                    output.WriteLine($"Owned member {pid} {member.ProcessName}: resident={resident}");
                    total = checked(total + resident);
                }
                return total;
            }
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }
}
