using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wf.Contracts.Agent.V1;
using Wf.TaskHost.Fixture;

namespace Wf.Integration.Tests.Agents;

public sealed class FootprintIntegrationTests(ITestOutputHelper output, FootprintIntegrationTests.PackageSnapshot package)
    : IClassFixture<FootprintIntegrationTests.PackageSnapshot>
{
    private const long MiB = 1024 * 1024;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task Native_idle_agent_meets_measured_cpu_and_Rss_budgets() => MeasureAsync(0);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "FootprintBenchmark")]
    public async Task Cold_native_agent_plus_actual_hosts_cost_at_most_50_decimal_MB_per_active_task(int maxTasks)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_AGENT_FOOTPRINT_BENCHMARK") == "true",
            "Set WF_AGENT_FOOTPRINT_BENCHMARK=true for the strict combined cold-process RSS benchmark.");
        await MeasureAsync(maxTasks);
    }

    private async Task MeasureAsync(int maxTasks)
    {
        var executable = Environment.GetEnvironmentVariable("WF_AGENT_EXECUTABLE");
        Assert.SkipUnless(executable is not null, "Set WF_AGENT_EXECUTABLE to a published Native AOT agent.");
        Assert.True(File.Exists(executable), $"Published agent not found: {executable}");
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "fp", Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(root);
        try
        {
            var archive = package.Archive ??= await ArchiveAsync(package.Launcher.EntryAssembly);
            var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
            output.WriteLine($"MaxTasks={maxTasks}; identical actual workflow archive SHA256={hash}; archive bytes={archive.Length}");
            var service = new FootprintGateway();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(service);
            await using var gateway = builder.Build();
            gateway.MapGrpcService<FootprintGateway>();
            await gateway.StartAsync(Token);
            var packagesBuilder = WebApplication.CreateBuilder();
            packagesBuilder.Logging.ClearProviders();
            packagesBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var packages = packagesBuilder.Build();
            packages.MapGet("/package.zip", () => Results.Bytes(archive, "application/zip"));
            await packages.StartAsync(Token);
            var start = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["Agent__Id"] = Guid.NewGuid().ToString();
            start.Environment["Agent__Name"] = "native-footprint";
            start.Environment["Agent__Pool"] = "test";
            start.Environment["Agent__Engines__0"] = Address(gateway).ToString();
            start.Environment["Agent__MaxTasks"] = Math.Max(1, maxTasks).ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Agent__ResultJournalDirectory"] = Path.Combine(root, "journal");
            start.Environment["Agent__PackageCache"] = Path.Combine(root, "packages");
            start.Environment["WF_FOOTPRINT_PROCESS_MARKERS"] = root;
            using var agent = Process.Start(start)!;
            var stdout = agent.StandardOutput.ReadToEndAsync(Token);
            var stderr = agent.StandardError.ReadToEndAsync(Token);
            int[] hostPids = [];
            try
            {
                await WaitAsync(() => Volatile.Read(ref service.Heartbeats) >= 2, agent);
                var idle = await SampleAsync(agent, [], TimeSpan.FromSeconds(20));
                Report("native idle", idle);
                Assert.True(idle.CpuPercent <= 1, $"Idle agent normalized CPU {idle.CpuPercent:F4}% exceeds 1%.");
                Assert.True(idle.AgentRss <= 100_000_000, $"Idle native agent RSS {idle.AgentRss}B exceeds 100 MB.");
                if (maxTasks == 0) return;
                var attempts = Enumerable.Range(0, maxTasks).Select(index => new Dispatch
                {
                    Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "hold", Attempt = 1 },
                    Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(Address(packages), "/package.zip").ToString(),
                    ReservationEpoch = 1, DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2)),
                    VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { marker = Path.Combine(root, $"host-{index}") }))
                }).ToArray();
                // Fixed 30s window begins BEFORE cold dispatch/download/startup, not at a favorable later instant.
                var measuring = SampleAsync(agent, [], TimeSpan.FromSeconds(30), root);
                foreach (var dispatch in attempts) service.Commands.Writer.TryWrite(new ControlMessage { Dispatch = dispatch });
                await WaitAsync(() => Enumerable.Range(0, maxTasks).All(index => File.Exists(Path.Combine(root, $"host-{index}"))) ||
                    !service.Results.IsEmpty, agent);
                Assert.Empty(service.Results);
                hostPids = await Task.WhenAll(Enumerable.Range(0, maxTasks).Select(index => ReadPidAsync(Path.Combine(root, $"host-{index}"), agent)));
                Assert.Equal(maxTasks, hostPids.Distinct().Count());
                var active = await measuring;
                Assert.Equal(maxTasks, active.HostRss.Count);
                Report($"{maxTasks} real cold SDK attempts", active);
                var combined = Math.Max(0, active.AggregateRss - idle.EndAgentRss);
                var perTaskBytes = (combined + maxTasks - 1) / maxTasks;
                output.WriteLine($"STRICT MaxTasks={maxTasks}; idle endpoint RSS={idle.EndAgentRss}B; peak simultaneous agent+hosts RSS={active.AggregateRss}B; " +
                    $"combined cold delta={combined}B; per active task={perTaskBytes}B = {perTaskBytes / (double)MiB:F4}MiB = {perTaskBytes / 1_000_000d:F4} decimal MB; " +
                    $"decimal50MB={(perTaskBytes <= 50_000_000 ? "PASS" : "FAIL")}");
                foreach (var host in active.HostRss)
                    output.WriteLine($"pid={host.Key}; full actual host max RSS={host.Value}B (no runtime/GC subtraction)");
                foreach (var dispatch in attempts)
                    service.Commands.Writer.TryWrite(new ControlMessage { Cancel = new Cancel { Task = dispatch.Task, Reason = "Footprint sample complete" } });
                await WaitAsync(() => service.Results.Count == maxTasks, agent);
                Assert.All(service.Results.Values, result => Assert.Equal(TaskOutcome.Cancelled, result.Outcome));
                await WaitAsync(() => hostPids.All(Stopped), agent);
                var after = await SampleAsync(agent, [], TimeSpan.FromSeconds(10));
                Report("native idle after task-host exit", after);
                Assert.True(after.CpuPercent <= 1, $"Post-task idle normalized CPU {after.CpuPercent:F4}% exceeds 1%.");
                Assert.True(after.AgentRss <= 100_000_000, $"Post-task idle native RSS {after.AgentRss}B exceeds 100 MB.");
                Assert.True(active.AgentRss <= 100_000_000, $"Active native-agent RSS {active.AgentRss}B exceeds its 100 MB budget.");
                Assert.True(perTaskBytes <= 50_000_000,
                    $"STRICT combined cold agent+actual-host increment {perTaskBytes}B/task ({perTaskBytes / (double)MiB:F4}MiB, {perTaskBytes / 1_000_000d:F4}MB) exceeds 50 MB.");
            }
            finally
            {
                if (!agent.HasExited) agent.Kill(entireProcessTree: true);
                await agent.WaitForExitAsync(Token);
                await Task.WhenAll(stdout, stderr);
                if (agent.ExitCode != 0) output.WriteLine($"Agent output: {await stdout}\n{await stderr}");
                foreach (var pid in hostPids) Assert.True(Stopped(pid), $"Task host {pid} survived agent cleanup.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task<Sample> SampleAsync(Process agent, int[] hostPids, TimeSpan duration, string? markerRoot = null)
    {
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(Token);
        sampling.CancelAfter(duration + TimeSpan.FromSeconds(10));
        var hosts = hostPids.ToDictionary(pid => pid, Process.GetProcessById);
        var hostRss = hostPids.ToDictionary(pid => pid, _ => 0L);
        try
        {
            agent.Refresh();
            var cpu = agent.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            long agentRss = 0;
            long aggregateRss = 0;
            var samples = 0;
            while (watch.Elapsed < duration)
            {
                Assert.False(agent.HasExited, "Native agent exited during footprint sample.");
                agent.Refresh();
                var currentAgentRss = agent.WorkingSet64;
                agentRss = Math.Max(agentRss, currentAgentRss);
                if (markerRoot is not null)
                    foreach (var marker in Directory.EnumerateFiles(markerRoot, "process-*.pid"))
                    {
                        try
                        {
                            if (int.TryParse(File.ReadAllText(marker), out var pid) && !hosts.ContainsKey(pid))
                            {
                                hosts.Add(pid, Process.GetProcessById(pid));
                                hostRss.Add(pid, 0);
                            }
                        }
                        catch (IOException) { }
                    }
                var currentAggregate = currentAgentRss;
                foreach (var (pid, host) in hosts)
                {
                    host.Refresh();
                    Assert.False(host.HasExited, $"Task host {pid} exited during footprint sample.");
                    var bytes = host.WorkingSet64;
                    hostRss[pid] = Math.Max(hostRss[pid], bytes);
                    currentAggregate += bytes;
                }
                aggregateRss = Math.Max(aggregateRss, currentAggregate);
                samples++;
                await Task.Delay(200, sampling.Token);
            }
            agent.Refresh();
            var cpuSeconds = (agent.TotalProcessorTime - cpu).TotalSeconds;
            // Process CPU normalized to total logical machine capacity, not single-core utilization.
            return new Sample(watch.Elapsed.TotalSeconds, cpuSeconds,
                100 * cpuSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount, agentRss,
                agent.WorkingSet64, aggregateRss, hostRss, samples);
        }
        finally { foreach (var process in hosts.Values) process.Dispose(); }
    }

    private void Report(string label, Sample sample) =>
        output.WriteLine($"{label}: wall={sample.WallSeconds:F3}s; process CPU={sample.CpuSeconds:F6}s; logical CPUs={Environment.ProcessorCount}; " +
            $"normalized CPU={sample.CpuPercent:F4}%; max native-agent RSS={sample.AgentRss} bytes; samples={sample.Samples} at 200ms");

    private async Task WaitAsync(Func<bool> condition, Process agent)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            Assert.False(agent.HasExited, $"Native agent exited with code {(agent.HasExited ? agent.ExitCode : -1)}.");
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task<int> ReadPidAsync(string marker, Process agent)
    {
        var pid = 0;
        await WaitAsync(() =>
        {
            try { return int.TryParse(File.ReadAllText(marker), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out pid); }
            catch (IOException) { return false; }
        }, agent);
        return pid;
    }

    private async Task<byte[]> ArchiveAsync(string entry)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Package the actual workflow runtime closure, not the integration test runner,
            // PostgreSQL drivers, test containers, and the control plane's unrelated dependencies.
            var fixture = typeof(FixtureMarker).Assembly.Location;
            using var deps = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(fixture, ".deps.json"), Token));
            var runtimeFiles = deps.RootElement.GetProperty("targets").EnumerateObject()
                .SelectMany(target => target.Value.EnumerateObject())
                .Where(library => library.Value.TryGetProperty("runtime", out _))
                .SelectMany(library => library.Value.GetProperty("runtime").EnumerateObject())
                .Select(asset => Path.GetFileName(asset.Name)).Distinct(StringComparer.Ordinal);
            foreach (var name in runtimeFiles) await AddAsync(Path.Combine(AppContext.BaseDirectory, name), name);
            await AddAsync(entry, "footprint-host.dll");
            await AddAsync(Path.ChangeExtension(fixture, ".runtimeconfig.json"), "footprint-host.runtimeconfig.json");
            await AddAsync(Path.ChangeExtension(fixture, ".deps.json"), "footprint-host.deps.json");
            await using var writer = new StreamWriter(archive.CreateEntry("wf-package.json").Open());
            await writer.WriteAsync("""{"version":"1.0.0","entryAssembly":"footprint-host.dll"}""");

            async Task AddAsync(string source, string name)
            {
                await using var input = File.OpenRead(source);
                await using var target = archive.CreateEntry(name).Open();
                await input.CopyToAsync(target, Token);
            }
        }
        return memory.ToArray();
    }

    private static bool Stopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private sealed record Sample(double WallSeconds, double CpuSeconds, double CpuPercent, long AgentRss, long EndAgentRss, long AggregateRss,
        IReadOnlyDictionary<int, long> HostRss, int Samples);

    public sealed class PackageSnapshot : IDisposable
    {
        public FootprintLauncher Launcher { get; } = new();
        public byte[]? Archive { get; set; }
        public void Dispose() => Launcher.Dispose();
    }

    public sealed class FootprintGateway : AgentGateway.AgentGatewayBase
    {
        public readonly Channel<ControlMessage> Commands = Channel.CreateUnbounded<ControlMessage>();
        public readonly ConcurrentDictionary<string, TaskCompleted> Results = new();
        public int Heartbeats;

        public override async Task Connect(IAsyncStreamReader<AgentMessage> requestStream,
            IServerStreamWriter<ControlMessage> responseStream, ServerCallContext context)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var sending = SendAsync();
            try
            {
                await foreach (var message in requestStream.ReadAllAsync(stop.Token))
                {
                    if (message.Heartbeat is not null) Interlocked.Increment(ref Heartbeats);
                    if (message.TaskCompleted is { } result) Results[result.Task.RunId] = result;
                    Commands.Writer.TryWrite(new ControlMessage { Ack = new Ack { Sequence = message.Sequence } });
                }
            }
            finally
            {
                await stop.CancelAsync();
                try { await sending; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }

            async Task SendAsync()
            {
                ulong sequence = 0;
                await foreach (var command in Commands.Reader.ReadAllAsync(stop.Token))
                {
                    command.Sequence = ++sequence;
                    command.LeaderEpoch = 1;
                    await responseStream.WriteAsync(command, stop.Token);
                }
            }
        }
    }
}
