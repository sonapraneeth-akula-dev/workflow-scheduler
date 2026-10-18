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
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Host.V1;
using Wf.Contracts.Logs;
using Wf.Contracts.Resources;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Reports;
using Wf.Integration.Tests.Logs;
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

    internal async Task RunAsync(string? mode)
    {
        var spooling = mode is "spool-running" or "spool-completed";
        var hardDeadline = mode == "spool-deadline";
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
        start.Environment["Agent__LogSpoolDirectory"] = Path.Combine(host.Root, "log-spool");
        var secret = "restart-spool-" + Guid.NewGuid().ToString("N");
        var localSecrets = new LocalSecretStore(Path.Combine(host.Root, "secrets"));
        start.Environment["Secrets__Provider"] = "Local";
        start.Environment["Secrets__LocalDirectory"] = Path.Combine(host.Root, "secrets");
        if (spooling) await localSecrets.WriteAsync(new("secret://local/credential"), secret, Token);
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
            var task = hardDeadline ? "restartDeadline" : spooling ? "restartSpool" : mode is null or "limits" ? "resourceMemory" : "drain";
            var timeout = hardDeadline ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(1);
            var attempt = await host.Seed(dispatch: false, task: task);
            var dispatch = new Dispatch
            {
                Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = task, Attempt = 1 },
                Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(host.Address, "/survival-package.zip").AbsoluteUri,
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow + timeout),
                VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { checkpointMarker = marker, amount = 0 }))
            };
            if (mode == "limits") dispatch.Limits = new() { CpuCores = 0.5, RamMb = 192 };
            if (spooling)
                dispatch.VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { checkpointMarker = marker,
                    amount = 0, credential = new { Name = "secret://local/credential" } }));
            dispatch.Grants.Add(host.App.Services.GetRequiredService<LogUploadCapabilities>()
                .Create(dispatch.Task, dispatch.DeadlineAt.ToDateTimeOffset()));
            using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry, new PlacementEngine(), TimeProvider.System);
            Assert.Equal(id, (await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
                new(attempt.RunId, "$post", task, 1, hash, new(new(pool: host.Pool)),
                    RequiredRamMb: mode == "limits" ? 192 : 0),
                dispatch, new ExecutionPolicy(timeout, new(), limits: mode == "limits" ? new(0.5, 192) : null), Token)).Agent!.Id);
            await Until(() => File.Exists(marker));
            await using (var ready = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var worker = await JsonDocument.ParseAsync(ready, cancellationToken: Token))
            {
                owned.Add(worker.RootElement.GetProperty("pid").GetInt32());
                if (hardDeadline) owned.Add(worker.RootElement.GetProperty("child").GetInt32());
            }
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
            if (hardDeadline)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(50));
                var certificate = OperatingSystem.IsWindows()
                    ? Path.Combine(Path.GetDirectoryName(path)!, "host-exited") : socketPath + ".exited";
                while (owned.Distinct().Any(IsRunning) || !File.Exists(certificate))
                    await Task.Delay(10, deadline.Token);
                var originalDeadline = root.GetProperty("Attempt").GetProperty("DeadlineAt").GetDateTimeOffset();
                Assert.InRange(DateTimeOffset.UtcNow - originalDeadline, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(40));
                var resultPath = Path.Combine(Path.GetDirectoryName(path)!, "spool", "result.pb");
                var timedOut = TaskCompleted.Parser.ParseFrom(await ReadMetadata(resultPath));
                Assert.Equal(TaskOutcome.TimedOut, timedOut.Outcome);
                Assert.Equal("Timeout", timedOut.Error.Category);
                Assert.Equal(["1"], await File.ReadAllLinesAsync(marker + ".starts", Token));
            }
            if (spooling)
            {
                await File.WriteAllTextAsync(marker + ".offline", "1", Token);
                using var offlineDeadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                offlineDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (!File.Exists(marker + ".offline-written")) await Task.Delay(10, offlineDeadline.Token);
                var spool = Path.Combine(Path.GetDirectoryName(path)!, "spool");
                AttemptRecord[] records = [];
                while (true)
                {
                    var snapshot = new List<AttemptRecord>();
                    foreach (var file in Directory.EnumerateFiles(spool, "*.record"))
                        snapshot.Add(AttemptRecord.Parser.ParseFrom(await ReadMetadata(file)));
                    records = snapshot.ToArray();
                    if (records.Count(record => record.Log?.Text.StartsWith("offline-", StringComparison.Ordinal) == true) == 4 &&
                        records.Any(record => record.Resource is { } sample &&
                            ResourceContract.Parse(sample.SampleJson.Memory).MachineUnavailableReason?.StartsWith(
                                "Agent disconnected;", StringComparison.Ordinal) == true)) break;
                    await Task.Delay(10, offlineDeadline.Token);
                }
                Assert.Equal(4, records.Count(record => record.Log?.Text.StartsWith("offline-", StringComparison.Ordinal) == true));
                foreach (var file in Directory.EnumerateFiles(spool).Where(file => !file.EndsWith(".next", StringComparison.Ordinal)))
                {
                    var bytes = await ReadMetadata(file);
                    Assert.Equal(-1, bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(secret)));
                    Assert.Equal(-1, bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)))));
                }
                await localSecrets.WriteAsync(new("secret://local/credential"), "rotated-" + Guid.NewGuid().ToString("N"), Token);
                Assert.Equal(["1"], await File.ReadAllLinesAsync(marker + ".starts", Token));
                if (mode == "spool-completed")
                {
                    await File.WriteAllTextAsync(marker + ".finish", "1", Token);
                    while (!File.Exists(Path.Combine(spool, "result.pb"))) await Task.Delay(10, offlineDeadline.Token);
                    var completed = TaskCompleted.Parser.ParseFrom(await ReadMetadata(Path.Combine(spool, "result.pb")));
                    Assert.Equal(TaskOutcome.Succeeded, completed.Outcome);
                    Assert.Contains("result-written-by-original-host", completed.OutputsJson.ToStringUtf8(), StringComparison.Ordinal);
                }
            }
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
            if (mode is "complete" or "limits" or "spool-running")
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
                await File.WriteAllTextAsync(marker + (spooling ? ".finish" : mode == "limits" ? ".go" : ".exit"), "1", Token);
            }
            await UntilAsync(async () => (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks["$post/" + task] ==
                (hardDeadline ? ExecutionStatus.TimedOut : mode == "complete" || spooling ? ExecutionStatus.Succeeded : ExecutionStatus.Failed));
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
                if (mode != "complete" && !spooling)
                    Assert.True(reader.GetInt16(1) == (short)(hardDeadline ? FailureCategory.Timeout :
                            mode == "limits" ? FailureCategory.ResourceLimit : FailureCategory.AgentLost),
                        $"Unexpected failure category {reader.GetInt16(1)}: {(reader.IsDBNull(4) ? "(no error)" : reader.GetString(4))}");
                Assert.Equal((short)0, reader.GetInt16(2));
                Assert.Equal((short)0, reader.GetInt16(3));
            }
            if (mode is "complete" or "limits" || spooling || hardDeadline)
                await Until(() => !Directory.GetFiles(Path.Combine(journal, "host-lifetimes", id.ToString("N")), "*.pending").Any());
            if (spooling || hardDeadline)
            {
                var indexPath = Path.Combine(host.Root, LogIndex.PrefixFor(dispatch.Task).Replace('/', Path.DirectorySeparatorChar), "index.json");
                await Until(() => File.Exists(indexPath));
                var index = JsonSerializer.Deserialize(await ReadMetadata(indexPath), LogJsonContext.Default.LogIndex)!;
                Assert.True(index.Complete);
                Assert.False(index.CaptureInterrupted);
                var frames = LogSpoolIntegrationTests.ReadFrames(host.Root, index);
                Assert.Equal((hardDeadline ? new[] { "before-hard-deadline" } :
                    ["before-restart", "offline-stdout ***", "offline-stderr ***", "offline-ctx ***", "offline-native-child", "finish ***"]).Order(),
                    frames.Select(frame => frame.Text).Order());
                Assert.Equal(hardDeadline ? 1 : 6, index.Lines);
                Assert.Equal(["1"], await File.ReadAllLinesAsync(marker + ".starts", Token));
                Assert.All(owned.Distinct(), pid => Assert.False(IsRunning(pid)));
                await using var samples = database.DataSource.CreateCommand(
                    "SELECT resource_sample_count FROM task_attempts WHERE run_id=$1 AND task_key=$2");
                samples.Parameters.AddWithValue(attempt.RunId);
                samples.Parameters.AddWithValue("$post/" + task);
                Assert.True((long)(await samples.ExecuteScalarAsync(Token))! >= 2);
                Assert.DoesNotContain(secret, Details(), StringComparison.Ordinal);
            }
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
