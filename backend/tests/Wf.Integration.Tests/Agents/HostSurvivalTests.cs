using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wf.Agent;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Reports;
using ReportHost = Wf.Integration.Tests.Reports.ReportPublishTests.ReportHost;

namespace Wf.Integration.Tests.Agents;

public sealed class HostSurvivalTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_agent_process_kill_preserves_packaged_SDK_host_and_durable_attempt_identity()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await ReportHost.Start(database.DataSource, key: key, configureServices: builder =>
        {
            builder.Services.AddSingleton<ResourceSampleSink>();
            builder.Services.AddSingleton(new LogUploadCapabilities(key, TimeProvider.System));
            builder.Services.AddSingleton(new BlobUploadGrants(key));
        }, configure: app => app.MapLogUploadEndpoints());
        var archive = await ReportPublishTests.Package();
        host.App.MapGet("/survival-package.zip", () => Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        var journal = Path.Combine(host.Root, "journal");
        var marker = Path.Combine(host.Root, "surviving-worker");
        var assembly = typeof(HostSurvivalTests).Assembly.Location;
        var executable = Environment.GetEnvironmentVariable("WF_AGENT_EXECUTABLE");
        var start = new ProcessStartInfo(executable ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = host.Root
        };
        if (executable is null)
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                         "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(AgentConnection).Assembly.Location })
                start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["Agent__Id"] = id.ToString("D");
        start.Environment["Agent__Name"] = $"survival-{id:N}";
        start.Environment["Agent__Pool"] = host.Pool;
        start.Environment["Agent__MaxTasks"] = "1";
        start.Environment["Agent__Engines__0"] = host.GatewayAddress.ToString();
        start.Environment["Agent__ResultJournalDirectory"] = journal;
        start.Environment["Agent__PackageCache"] = Path.Combine(host.Root, "packages");
        if (Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT") is { } delegated)
            start.Environment["Agent__LinuxCgroupRoot"] = delegated;
        using var agent = Process.Start(start) ?? throw new InvalidOperationException("Actual agent did not start.");
        var diagnostics = new ConcurrentQueue<string>();
        agent.OutputDataReceived += (_, line) => { if (line.Data is not null) diagnostics.Enqueue(line.Data); };
        agent.ErrorDataReceived += (_, line) => { if (line.Data is not null) diagnostics.Enqueue(line.Data); };
        agent.BeginOutputReadLine();
        agent.BeginErrorReadLine();
        var owned = new List<int>();
        string? workloadCgroup = null;
        string? socketPath = null;
        try
        {
            await Until(() => host.Registry.Find(id) is not null);
            var attempt = await host.Seed(dispatch: false, task: "resourceMemory");
            var dispatch = new Dispatch
            {
                Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = "resourceMemory", Attempt = 1 },
                Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(host.Address, "/survival-package.zip").AbsoluteUri,
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
                VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { checkpointMarker = marker, amount = 0 }))
            };
            dispatch.Grants.Add(host.App.Services.GetRequiredService<LogUploadCapabilities>()
                .Create(dispatch.Task, dispatch.DeadlineAt.ToDateTimeOffset()));
            using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry, new PlacementEngine(), TimeProvider.System);
            Assert.Equal(id, (await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
                new(attempt.RunId, "$post", "resourceMemory", 1, hash, new(new(pool: host.Pool))),
                dispatch, Token)).Agent!.Id);
            await Until(() => File.Exists(marker));
            await using (var ready = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var worker = await JsonDocument.ParseAsync(ready, cancellationToken: Token))
                owned.AddRange([worker.RootElement.GetProperty("pid").GetInt32(), worker.RootElement.GetProperty("supervisor").GetInt32()]);
            var path = Assert.Single(Directory.GetFiles(Path.Combine(journal, "host-lifetimes", id.ToString("N")),
                "attempt.json", SearchOption.AllDirectories));
            var before = await File.ReadAllBytesAsync(path, Token);
            using var record = JsonDocument.Parse(before);
            var root = record.RootElement;
            workloadCgroup = root.GetProperty("WorkloadCgroup").GetString();
            socketPath = root.GetProperty("SocketPath").GetString();
            Assert.Equal(owned[^1], root.GetProperty("Pid").GetInt32());
            if (OperatingSystem.IsLinux())
            {
                var stat = await File.ReadAllTextAsync($"/proc/{owned[^1]}/stat", Token);
                var started = ulong.Parse(stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19],
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(started, root.GetProperty("StartIdentity").GetUInt64());
            }
            else
            {
                using var supervisor = Process.GetProcessById(owned[^1]);
                Assert.Equal(supervisor.StartTime.ToFileTimeUtc(), checked((long)root.GetProperty("StartIdentity").GetUInt64()));
                Assert.Equal(supervisor.StartTime.ToUniversalTime().Ticks, root.GetProperty("StartedUtcTicks").GetInt64());
            }
            Assert.True(File.Exists(root.GetProperty("SocketPath").GetString()));
            Assert.Equal(attempt.RunId.ToString("D"), root.GetProperty("Attempt").GetProperty("RunId").GetString());
            Assert.Equal("resourceMemory", root.GetProperty("Attempt").GetProperty("TaskKey").GetString());
            Assert.Equal(1UL, root.GetProperty("Attempt").GetProperty("ReservationEpoch").GetUInt64());
            if (OperatingSystem.IsWindows())
                Assert.StartsWith("Local\\wf-task-host-", root.GetProperty("JobName").GetString());
            agent.Kill(entireProcessTree: false);
            // Infinite/async waits also drain pipes still held by the surviving SDK host.
            Assert.True(agent.WaitForExit(5_000));
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
            foreach (var pid in owned.Distinct())
            {
                using var process = Process.GetProcessById(pid);
                Assert.False(process.HasExited, $"SDK host {pid} died with the actual agent process.");
            }
            Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
            if (Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT") is not null)
            {
                Assert.NotNull(workloadCgroup);
                Assert.Contains(owned[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    await File.ReadAllLinesAsync(Path.Combine(workloadCgroup, "cgroup.procs"), Token));
            }
        }
        finally
        {
            if (!agent.HasExited) agent.Kill(entireProcessTree: true);
            foreach (var pid in owned.Distinct())
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(Token);
                }
                catch (ArgumentException) { }
            }
            await agent.WaitForExitAsync(Token);
            if (workloadCgroup is not null) Directory.Delete(workloadCgroup);
            if (socketPath is not null)
            {
                File.Delete(socketPath);
                File.Delete(socketPath + ".lifetime");
                Directory.Delete(Path.GetDirectoryName(socketPath)!);
            }
        }

        async Task Until(Func<bool> condition)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            while (!condition())
            {
                Assert.False(agent.HasExited, $"Actual agent exited: {string.Join(Environment.NewLine, diagnostics)}");
                await Task.Delay(10, deadline.Token);
            }
        }
    }
}
