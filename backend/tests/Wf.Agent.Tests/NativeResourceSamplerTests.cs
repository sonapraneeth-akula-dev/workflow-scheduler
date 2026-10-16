using System.Globalization;
using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Wf.Agent.Hosts;
using Wf.Agent.Sampling;
using Wf.Contracts.Agent.V1;
using Wf.Agent.ResourceFixture;

namespace Wf.Agent.Tests;

public sealed class NativeResourceSamplerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Actual_concurrent_owned_task_UDP_bytes_directions_rates_and_child_scope_are_exact(bool ipv6, bool child)
    {
        var delegated = Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT");
        Assert.SkipWhen(OperatingSystem.IsLinux() && delegated is null,
            "Native counter integration requires an isolated delegated cgroup and BPF/NET_ADMIN capabilities.");
        using var reader = ResourceSampler.Create(new NativeSamplingOptions(true, delegated));
        using var service = new ResourceSamplingService(reader, TimeProvider.System, NullLogger<ResourceSamplingService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, sampling: service);
        var root = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_ARTIFACTS") ??
            Path.Combine(".agent-artifacts", "native-resource-tests"), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var package = new TaskHostPackage("1.0.0", "dotnet",
            ["exec", "--runtimeconfig", RuntimeConfig, "--depsfile", DepsFile, typeof(ResourceFixtureMarker).Assembly.Location],
            AppContext.BaseDirectory);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(35));
        var first = Request("client", true, 137, 311);
        var second = Request("server", false, 311, 137);
        Task<TaskCompleted>? client = null, server = null;
        try
        {
            await pool.PrewarmAsync(package, stop.Token);
            await pool.PrewarmAsync(package, stop.Token);
            var clientDispatch = Dispatch(first);
            var serverDispatch = Dispatch(second);
            client = pool.ExecuteAsync(package, clientDispatch, cancellationToken: stop.Token);
            server = pool.ExecuteAsync(package, serverDispatch, cancellationToken: stop.Token);
            await WaitAsync(() => File.Exists(first.Ready) && File.Exists(second.Ready), stop.Token);
            var clientReady = JsonSerializer.Deserialize<TrafficReady>(await File.ReadAllTextAsync(first.Ready, stop.Token))!;
            var serverReady = JsonSerializer.Deserialize<TrafficReady>(await File.ReadAllTextAsync(second.Ready, stop.Token))!;
            Assert.NotEqual(clientReady.ProcessId, serverReady.ProcessId);
            if (OperatingSystem.IsLinux())
                foreach (var ready in new[] { clientReady, serverReady })
                {
                    Assert.Equal("0000000000000000", ready.Capabilities);
                    Assert.True(ready.NoNewPrivileges);
                }
            var baseline = service.ActiveTasks.ToDictionary(task => task.RunId, task => task.Sample);
            Assert.Equal(2, baseline.Count);
            foreach (var task in service.ActiveTasks)
            {
                var ready = task.RunId == clientDispatch.Task.RunId ? clientReady : serverReady;
                if (child) Assert.NotEqual(task.ProcessId, ready.ProcessId);
                else Assert.Equal(task.ProcessId, ready.ProcessId);
            }
            foreach (var sample in baseline.Values)
            {
                Assert.Null(sample.NetworkUnavailableReason);
                Assert.NotNull(sample.Network);
                Assert.Equal(0UL, sample.Network.Sent);
                Assert.Equal(0UL, sample.Network.Received);
                Assert.True(sample.RamBytes > 0);
                Assert.Equal(OperatingSystem.IsWindows() ? "job-with-descendants" : "cgroup-with-descendants", sample.CpuScope);
            }
            await WriteAsync(first.Go, serverReady.Port.ToString(CultureInfo.InvariantCulture), stop.Token);
            await WriteAsync(second.Go, clientReady.Port.ToString(CultureInfo.InvariantCulture), stop.Token);
            await WaitAsync(() => File.Exists(first.Done) && File.Exists(second.Done), stop.Token);
            var overhead = OperatingSystem.IsLinux() ? ipv6 ? 48 : 28 : 0;
            var clientSent = child && OperatingSystem.IsWindows() ? 0UL : (ulong)(first.Count * (first.SendBytes + overhead));
            var clientReceived = child && OperatingSystem.IsWindows() ? 0UL : (ulong)(first.Count * (first.ReceiveBytes + overhead));
            var serverSent = child && OperatingSystem.IsWindows() ? 0UL : (ulong)(second.Count * (second.SendBytes + overhead));
            var serverReceived = child && OperatingSystem.IsWindows() ? 0UL : (ulong)(second.Count * (second.ReceiveBytes + overhead));
            using var countersReady = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            countersReady.CancelAfter(TimeSpan.FromSeconds(8));
            try { await WaitAsync(() =>
            {
                foreach (var task in service.ActiveTasks)
                {
                    Assert.Null(task.Sample.NetworkUnavailableReason);
                    Assert.Null(task.Sample.Error);
                    Assert.NotNull(task.Sample.Network);
                    var old = baseline[task.RunId];
                    if (task.Sample.At != old.At)
                    {
                        if (old.Network is not null && task.Sample.Interval is { } interval)
                        {
                            Assert.Equal((task.Sample.Network.Sent - old.Network.Sent) / interval.TotalSeconds,
                                task.Sample.BytesSentPerSecond!.Value, precision: 7);
                            Assert.Equal((task.Sample.Network.Received - old.Network.Received) / interval.TotalSeconds,
                                task.Sample.BytesReceivedPerSecond!.Value, precision: 7);
                        }
                        baseline[task.RunId] = task.Sample;
                    }
                }
                var samples = service.ActiveTasks.ToDictionary(task => task.RunId, task => task.Sample);
                return samples.Values.All(sample => sample.CpuPercent is not null) &&
                    samples[clientDispatch.Task.RunId].Network == new NetworkBytes(clientSent, clientReceived) &&
                    samples[serverDispatch.Task.RunId].Network == new NetworkBytes(serverSent, serverReceived);
            }, countersReady.Token); }
            catch (OperationCanceledException) when (countersReady.IsCancellationRequested)
            {
                Assert.Fail($"Owned task counts: {string.Join("; ", service.ActiveTasks.Select(task => $"{task.RunId}={task.Sample.Network}, error={task.Sample.NetworkUnavailableReason}"))}; " +
                    $"expected client={clientSent}/{clientReceived}, server={serverSent}/{serverReceived}.");
            }
            foreach (var sample in service.ActiveTasks)
            {
                Assert.NotNull(sample.Sample.CpuPercent);
                Assert.Equal(OperatingSystem.IsWindows() ? "host-process-etw-transport" : "cgroup-skb-l3-with-descendants",
                    sample.Sample.NetworkScope);
            }
            await File.WriteAllTextAsync(first.Stop, "stop", stop.Token);
            await File.WriteAllTextAsync(second.Stop, "stop", stop.Token);
            Assert.Equal(TaskOutcome.Succeeded, (await client).Outcome);
            Assert.Equal(TaskOutcome.Succeeded, (await server).Outcome);
            Assert.Empty(service.ActiveTasks);
        }
        finally
        {
            await stop.CancelAsync();
            if (client is not null) try { await client; } catch (Exception) { }
            if (server is not null) try { await server; } catch (Exception) { }
            await service.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }

        TrafficRequest Request(string name, bool client, int sent, int received) => new(
            Path.Combine(root, name + ".ready"), Path.Combine(root, name + ".go"),
            Path.Combine(root, name + ".done"), Path.Combine(root, name + ".stop"),
            client, ipv6, 3, sent, received, child, RuntimeConfig, DepsFile);
    }

    [Fact]
    public void Disabled_native_accounting_is_explicit_and_does_not_create_an_isolation_scope()
    {
        using var reader = ResourceSampler.Create();
        Assert.Null(reader.TryCreateScope(out var reason));
        Assert.Contains("Agent:Sampling:Enabled", reason);
    }

    [Fact]
    public void Invalid_linux_delegation_is_explicit_not_an_ordinary_file_counter_substitute()
    {
        using var reader = new LinuxResourceSampler(new NativeSamplingOptions(true, AppContext.BaseDirectory));
        Assert.Null(reader.TryCreateScope(out var reason));
        Assert.Contains(OperatingSystem.IsLinux() ? "cgroup-v2" : "Linux", reason);
    }

    [Fact]
    public async Task Simulated_native_access_denial_keeps_actual_task_execution_and_explicit_unavailable_metrics()
    {
        using var reader = new DeniedScopeSampler();
        await EchoAsync(reader, sample =>
        {
            Assert.Null(sample.Network);
            Assert.Contains("scripted native access denied", sample.NetworkUnavailableReason);
            Assert.Equal("process", sample.CpuScope);
        });
    }

    [Fact]
    public async Task Native_resource_rights_state_does_not_stop_actual_task()
    {
        var root = Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT");
        Assert.SkipWhen(OperatingSystem.IsLinux() && root is null, "Requires isolated cgroup delegation.");
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_IDENTITY") is { } identity)
        {
            var status = File.ReadLines("/proc/self/status").Where(line =>
                    line.StartsWith("Uid:", StringComparison.Ordinal) || line.StartsWith("CapEff:", StringComparison.Ordinal) ||
                    line.StartsWith("NoNewPrivs:", StringComparison.Ordinal))
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToDictionary(parts => parts[0], parts => parts[1]);
            Assert.Equal("65534", status["Uid:"]);
            Assert.Equal(identity == "without-bpf" ? "0000000000000000" : "0000008000001000", status["CapEff:"]);
            Assert.Equal("1", status["NoNewPrivs:"]);
        }
        using var reader = ResourceSampler.Create(new NativeSamplingOptions(true, root));
        await EchoAsync(reader, sample =>
        {
            Assert.Equal(OperatingSystem.IsWindows() ? "job-with-descendants" : "cgroup-with-descendants", sample.CpuScope);
            if (Environment.GetEnvironmentVariable("WF_RESOURCE_EXPECT_UNAVAILABLE") == "1")
            {
                Assert.Null(sample.Network);
                Assert.Contains("BPF_MAP_CREATE", sample.NetworkUnavailableReason);
            }
            else
            {
                Assert.Null(sample.NetworkUnavailableReason);
                Assert.NotNull(sample.Network);
            }
        });
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Native_options_are_explicit_and_independent_of_task_report_configuration(string configured, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Sampling:Enabled"] = configured, ["Agent:Sampling:LinuxCgroupRoot"] = AppContext.BaseDirectory
        }).Build();
        var options = NativeSamplingOptions.FromConfiguration(configuration);
        Assert.Equal(expected, options.Enabled);
        Assert.Equal(AppContext.BaseDirectory, options.LinuxCgroupRoot);
    }

    [Fact]
    public void Relative_delegation_root_is_rejected_before_any_native_state_change()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Sampling:Enabled"] = "true", ["Agent:Sampling:LinuxCgroupRoot"] = "relative"
        }).Build();
        Assert.Throws<ArgumentException>(() => NativeSamplingOptions.FromConfiguration(configuration));
    }

    private static async Task EchoAsync(ResourceSampler reader, Action<ResourceMeasurement> verify)
    {
        using var service = new ResourceSamplingService(reader, TimeProvider.System, NullLogger<ResourceSamplingService>.Instance);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance, sampling: service);
        var package = new TaskHostPackage("1.0.0", "dotnet",
            ["exec", "--runtimeconfig", RuntimeConfig, "--depsfile", DepsFile, typeof(ResourceFixtureMarker).Assembly.Location],
            AppContext.BaseDirectory);
        var dispatch = new Dispatch
        {
            Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "echo", Attempt = 1 },
            Version = "1.0.0", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(20)),
            InputsJson = ByteString.CopyFromUtf8("""{"source":{"value":"unused"}}""")
        };
        var called = false;
        var result = await pool.ExecuteAsync(package, dispatch, cancellationToken: TestContext.Current.CancellationToken,
            journalResult: _ =>
            {
                var sample = Assert.Single(service.ActiveTasks).Sample;
                Assert.True(sample.RamBytes > 0);
                Assert.Null(sample.Error);
                verify(sample);
                called = true;
            });
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        if (OperatingSystem.IsLinux() && reader is LinuxResourceSampler)
        {
            using var output = JsonDocument.Parse(result.OutputsJson.Memory);
            Assert.Equal("0000000000000000", output.RootElement.GetProperty("capabilities").GetString());
            Assert.True(output.RootElement.GetProperty("noNewPrivileges").GetBoolean());
        }
        Assert.True(called);
        Assert.Empty(service.ActiveTasks);
    }

    private sealed class DeniedScopeSampler() : ResourceSampler(new NativeSamplingOptions(true))
    {
        protected override ITaskResourceScope CreateScope() => throw new Win32Exception(5, "scripted native access denied");
        public override ResourceCounters ReadMachine() => throw new NotSupportedException();
    }

    private static string RuntimeConfig => Path.ChangeExtension(typeof(NativeResourceSamplerTests).Assembly.Location, ".runtimeconfig.json");
    private static string DepsFile => Path.ChangeExtension(typeof(NativeResourceSamplerTests).Assembly.Location, ".deps.json");

    private static Dispatch Dispatch(TrafficRequest request) => new()
    {
        Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "traffic", Attempt = 1 },
        Version = "1.0.0", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(35)),
        InputsJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { source = new { value = JsonSerializer.Serialize(request) } }))
    };

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(10, token);
    }

    private static async Task WriteAsync(string path, string text, CancellationToken token)
    {
        await File.WriteAllTextAsync(path + ".new", text, token);
        File.Move(path + ".new", path);
    }
}
