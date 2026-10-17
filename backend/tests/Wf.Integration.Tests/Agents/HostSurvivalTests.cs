using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wf.Agent;
using Wf.Contracts.Agent.V1;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Reports;
using ReportHost = Wf.Integration.Tests.Reports.ReportPublishTests.ReportHost;

namespace Wf.Integration.Tests.Agents;

[Collection("Agent process containment")]
public sealed class HostSurvivalTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task Real_agent_process_kill_preserves_packaged_SDK_host_and_durable_attempt_identity() => RunAsync(null);

    [Theory]
    [InlineData("complete")]
    [InlineData("limits")]
    [InlineData("dead")]
    [InlineData("reused")]
    public Task Real_agent_restart_readopts_original_attempt_before_Hello_or_reports_AgentLost(string mode)
    {
        Assert.SkipWhen(OperatingSystem.IsLinux() && mode == "limits" &&
            Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT") is null,
            "Requires an isolated delegated cgroup-v2 root.");
        return RunAsync(mode);
    }

    private async Task RunAsync(string? mode)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await ReportHost.Start(database.DataSource, key: key, configureServices: builder =>
        {
            builder.Services.AddSingleton<ResourceSampleSink>();
            builder.Services.AddSingleton(new LogUploadCapabilities(key, TimeProvider.System));
            builder.Services.AddSingleton(new BlobUploadGrants(key));
            builder.Services.AddSingleton<AgentLossSweeper>();
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
        Process? restarted = null;
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
            var session = host.Registry.Find(id)!.SessionId;
            var task = mode is null or "limits" ? "resourceMemory" : "drain";
            var attempt = await host.Seed(dispatch: false, task: task);
            var dispatch = new Dispatch
            {
                Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = task, Attempt = 1 },
                Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(host.Address, "/survival-package.zip").AbsoluteUri,
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
                VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { checkpointMarker = marker, amount = 0 }))
            };
            if (mode == "limits") dispatch.Limits = new() { CpuCores = 0.5, RamMb = 192 };
            dispatch.Grants.Add(host.App.Services.GetRequiredService<LogUploadCapabilities>()
                .Create(dispatch.Task, dispatch.DeadlineAt.ToDateTimeOffset()));
            using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry, new PlacementEngine(), TimeProvider.System);
            Assert.Equal(id, (await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
                new(attempt.RunId, "$post", task, 1, hash, new(new(pool: host.Pool)),
                    RequiredRamMb: mode == "limits" ? 192 : 0),
                dispatch, new ExecutionPolicy(TimeSpan.FromMinutes(1), new(), limits: mode == "limits" ? new(0.5, 192) : null), Token)).Agent!.Id);
            await Until(() => File.Exists(marker));
            await using (var ready = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var worker = await JsonDocument.ParseAsync(ready, cancellationToken: Token))
                owned.Add(worker.RootElement.GetProperty("pid").GetInt32());
            var path = Assert.Single(Directory.GetFiles(Path.Combine(journal, "host-lifetimes", id.ToString("N")),
                "attempt.json", SearchOption.AllDirectories));
            var before = await ReadMetadata(path);
            using var record = JsonDocument.Parse(before);
            var root = record.RootElement;
            workloadCgroup = root.GetProperty("WorkloadCgroup").GetString();
            socketPath = root.GetProperty("SocketPath").GetString();
            owned.Add(root.GetProperty("Pid").GetInt32());
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
            Assert.Equal(task, root.GetProperty("Attempt").GetProperty("TaskKey").GetString());
            Assert.Equal(1UL, root.GetProperty("Attempt").GetProperty("ReservationEpoch").GetUInt64());
            if (OperatingSystem.IsWindows())
                Assert.StartsWith("Local\\wf-task-host-", root.GetProperty("JobName").GetString());
            agent.Kill(entireProcessTree: false);
            // Infinite/async waits also drain pipes still held by the surviving SDK host.
            Assert.True(agent.WaitForExit(5_000));
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
            foreach (var pid in owned.Distinct())
            {
                Assert.True(IsRunning(pid), $"SDK host {pid} died with the actual agent process: {Details()}");
            }
            using var survived = JsonDocument.Parse(await ReadMetadata(path));
            foreach (var property in new[] { "Pid", "StartIdentity", "SocketPath", "ReceiptToken", "DispatchBase64" })
                Assert.Equal(root.GetProperty(property).GetRawText(), survived.RootElement.GetProperty(property).GetRawText());
            if (Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT") is not null)
            {
                Assert.NotNull(workloadCgroup);
                Assert.Contains(owned[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    await File.ReadAllLinesAsync(Path.Combine(workloadCgroup, "cgroup.procs"), Token));
            }
            if (mode is null) return;
            if (mode == "dead")
            {
                using var supervisor = Process.GetProcessById(owned[^1]);
                supervisor.Kill(entireProcessTree: true);
                Assert.True(supervisor.WaitForExit(5_000));
            }
            if (mode == "reused")
            {
                var changed = JsonNode.Parse(before)!;
                changed["StartIdentity"] = root.GetProperty("StartIdentity").GetUInt64() + 1;
                await File.WriteAllTextAsync(path, changed.ToJsonString(), Token);
            }
            var watch = Stopwatch.StartNew();
            restarted = Process.Start(start) ?? throw new InvalidOperationException("Replacement agent did not start.");
            restarted.OutputDataReceived += (_, line) => { if (line.Data is not null) diagnostics.Enqueue(line.Data); };
            restarted.ErrorDataReceived += (_, line) => { if (line.Data is not null) diagnostics.Enqueue(line.Data); };
            restarted.BeginOutputReadLine();
            restarted.BeginErrorReadLine();
            await Until(() => host.Registry.Find(id) is { } current && current.SessionId != session);
            Assert.True(watch.Elapsed <= TimeSpan.FromSeconds(10), $"Actual agent startup/re-adoption took {watch.Elapsed}.");
            if (mode is "complete" or "limits")
            {
                Assert.Equal(1, host.Registry.Find(id)!.SlotsUsed);
                Assert.Equal(ExecutionStatus.Running,
                    (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks["$post/" + task]);
                using var resumed = JsonDocument.Parse(await ReadMetadata(path));
                foreach (var property in new[] { "Pid", "StartIdentity", "SocketPath", "ReceiptToken", "DispatchBase64" })
                    Assert.Equal(root.GetProperty(property).GetRawText(), resumed.RootElement.GetProperty(property).GetRawText());
                foreach (var pid in owned.Distinct())
                {
                    Assert.True(IsRunning(pid), $"Original SDK host {pid} exited during re-adoption: {Details()}");
                }
                await File.WriteAllTextAsync(marker + (mode == "limits" ? ".go" : ".exit"), "1", Token);
            }
            await UntilAsync(async () => (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks["$post/" + task] ==
                (mode == "complete" ? ExecutionStatus.Succeeded : ExecutionStatus.Failed));
            await using (var query = database.DataSource.CreateCommand("""
                SELECT count(*),min(failure_category),min(author_retries),min(platform_retries),min(error)
                FROM task_attempts WHERE run_id=$1 AND task_key=$2
                """))
            {
                query.Parameters.AddWithValue(attempt.RunId);
                query.Parameters.AddWithValue("$post/" + task);
                await using var reader = await query.ExecuteReaderAsync(Token);
                Assert.True(await reader.ReadAsync(Token));
                Assert.Equal(1L, reader.GetInt64(0));
                if (mode != "complete")
                    Assert.True(reader.GetInt16(1) == (short)(mode == "limits" ? FailureCategory.ResourceLimit : FailureCategory.AgentLost),
                        $"Unexpected failure category {reader.GetInt16(1)}: {(reader.IsDBNull(4) ? "(no error)" : reader.GetString(4))}");
                Assert.Equal((short)0, reader.GetInt16(2));
                Assert.Equal((short)0, reader.GetInt16(3));
            }
            if (mode is "complete" or "limits")
                await Until(() => !Directory.GetFiles(Path.Combine(journal, "host-lifetimes", id.ToString("N")), "*.pending").Any());
        }
        finally
        {
            if (!agent.HasExited) agent.Kill(entireProcessTree: true);
            if (restarted is { HasExited: false }) restarted.Kill(entireProcessTree: true);
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
            if (restarted is not null) { await restarted.WaitForExitAsync(Token); restarted.Dispose(); }
            if (workloadCgroup is not null && Directory.Exists(workloadCgroup)) Directory.Delete(workloadCgroup);
            if (socketPath is not null && Directory.Exists(Path.GetDirectoryName(socketPath)!))
            {
                foreach (var suffix in new[] { "", ".lifetime", ".control", ".exited" }) File.Delete(socketPath + suffix);
                Directory.Delete(Path.GetDirectoryName(socketPath)!);
            }
        }

        async Task Until(Func<bool> condition)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            while (!condition())
            {
                Assert.False((restarted ?? agent).HasExited, $"Actual agent exited: {string.Join(Environment.NewLine, diagnostics)}");
                try { await Task.Delay(10, deadline.Token); }
                catch (OperationCanceledException error) when (!Token.IsCancellationRequested)
                { throw new TimeoutException("Actual agent condition did not complete: " + Details(), error); }
            }
        }

        string Details() => string.Join(Environment.NewLine, diagnostics.TakeLast(80));

        async Task UntilAsync(Func<Task<bool>> condition)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            while (!await condition())
            {
                Assert.False((restarted ?? agent).HasExited, $"Actual agent exited: {string.Join(Environment.NewLine, diagnostics)}");
                try { await Task.Delay(10, deadline.Token); }
                catch (OperationCanceledException error) when (!Token.IsCancellationRequested)
                { throw new TimeoutException("Actual agent durable condition did not complete: " + Details(), error); }
            }
        }

    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<byte[]> ReadMetadata(string path)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var bytes = new MemoryStream();
        await source.CopyToAsync(bytes, Token);
        return bytes.ToArray();
    }
}
