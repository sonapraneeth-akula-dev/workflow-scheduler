using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Wf.Agent.Hosts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Host.V1;

namespace Wf.Agent.Tests;

public sealed class HostReadoptionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("fence")]
    [InlineData("reused")]
    [InlineData("dead")]
    [InlineData("authentication")]
    [InlineData("exclusive")]
    public async Task Actual_killed_parent_host_is_exclusively_authenticated_and_resumed_without_delegate_reexecution(string scenario)
    {
        await using var crash = await Crash.StartAsync();
        var record = crash.Record;
        if (scenario == "reused")
        {
            record = record with { StartIdentity = record.StartIdentity + 1 };
            await File.WriteAllTextAsync(crash.Metadata, JsonSerializer.Serialize(record,
                TaskHostAttemptJson.Default.TaskHostAttempt), Token);
        }
        if (scenario == "dead")
        {
            using var process = Process.GetProcessById(record.Pid);
            process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit(5_000));
        }
        var logging = new RecoveryLog();
        var pool = new TaskHostPool(logging, containmentDirectory: Path.Combine(crash.Root, "recovery"));
        var quarantined = scenario == "dead" || OperatingSystem.IsLinux() && scenario == "reused";
        try
        {
            var watch = Stopwatch.StartNew();
            var restored = Assert.Single(await pool.RecoverAsync((package, _) => Task.FromResult(package), Token));
            Assert.True(watch.Elapsed <= TimeSpan.FromSeconds(10), $"Re-adoption took {watch.Elapsed}.");
            Assert.Equal(crash.Dispatch, restored.Dispatch);
            Assert.Equal(0, pool.ProcessStartCount);
            Assert.Equal(["1"], await File.ReadAllLinesAsync(crash.Marker + ".starts", Token));
            if (scenario is "reused" or "dead")
            {
                Assert.NotNull(restored.Error);
                Assert.Throws<InvalidOperationException>(() => pool.RecoveredPackage(crash.Dispatch.Task));
                if (quarantined)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => pool.FenceAsync(Token));
                else await pool.FenceAsync(Token);
                return;
            }
            Assert.True(restored.Error is null, string.Join(Environment.NewLine, logging.Errors));
            if (scenario == "authentication")
            {
                using var channel = Channel(record.SocketPath, "incorrect");
                var error = await Assert.ThrowsAsync<RpcException>(async () =>
                {
                    using var call = new Wf.Contracts.Host.V1.TaskHost.TaskHostClient(channel).ReadyAsync(
                        new ReadyRequest(), cancellationToken: Token);
                    await call.ResponseAsync;
                });
                Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
            }
            if (scenario == "exclusive")
            {
                await using var contender = PhysicalHostLifetimeTests.Pool(crash.Root);
                await Assert.ThrowsAsync<InvalidOperationException>(() => contender.RecoverAsync(
                    (package, _) => Task.FromResult(package), Token));
            }
            if (scenario == "fence")
            {
                await pool.FenceAsync(Token);
                Assert.Throws<InvalidOperationException>(() => pool.RecoveredPackage(crash.Dispatch.Task));
            }
            else
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
                var execution = pool.ExecuteAsync(pool.RecoveredPackage(crash.Dispatch.Task), crash.Dispatch,
                    cancellationGrace: TimeSpan.FromMilliseconds(100), cancellationToken: cancellation.Token);
                if (scenario == "cancel") await cancellation.CancelAsync();
                else await File.WriteAllTextAsync(crash.Marker + ".release", "1", Token);
                TaskCompleted? result = null;
                try { result = await execution; }
                catch (OperationCanceledException) when (scenario == "cancel" && cancellation.IsCancellationRequested) { }
                if (result is not null)
                    Assert.Equal(scenario == "cancel" ? TaskOutcome.Cancelled : TaskOutcome.Succeeded, result.Outcome);
                if (scenario != "cancel")
                {
                    Assert.NotNull(result);
                    using var outputs = JsonDocument.Parse(result.OutputsJson.Memory);
                    Assert.Equal(crash.Worker, outputs.RootElement.GetProperty("pid").GetInt32());
                }
            }
            Assert.Equal(["1"], await File.ReadAllLinesAsync(crash.Marker + ".starts", Token));
            Assert.Empty(Directory.GetFiles(Path.Combine(crash.Root, "recovery"), "*.pending"));
            Assert.False(IsRunning(crash.Worker));
            Assert.False(IsRunning(crash.Child));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    private sealed class RecoveryLog : ILogger<TaskHostPool>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Errors.Add(formatter(state, exception) + Environment.NewLine + exception);
        }
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("wrong-epoch")]
    [InlineData("oversized")]
    public async Task Invalid_metadata_remains_quarantined_and_cannot_be_blindly_cleared(string mode)
    {
        await using var crash = await Crash.StartAsync();
        var value = mode switch
        {
            "invalid-json" => "{",
            "oversized" => new string('x', 6 * 1024 * 1024 + 1),
            _ => JsonSerializer.Serialize(crash.Record with
            {
                Attempt = crash.Record.Attempt! with { ReservationEpoch = 2 }
            }, TaskHostAttemptJson.Default.TaskHostAttempt)
        };
        await File.WriteAllTextAsync(crash.Metadata, value, Token);
        await using var pool = PhysicalHostLifetimeTests.Pool(crash.Root);
        await Assert.ThrowsAnyAsync<Exception>(() => pool.RecoverAsync(
            (package, _) => Task.FromResult(package), Token));
        Assert.Single(Directory.GetFiles(Path.Combine(crash.Root, "recovery"), "*.pending"));
        Assert.True(IsRunning(crash.Worker));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.FenceAsync(Token));
    }

    private static GrpcChannel Channel(string path, string token)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("x-wf-host-token", token);
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpClient = http, DisposeHttpClient = true });
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (!OperatingSystem.IsLinux()) return true;
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return !stat[(stat.LastIndexOf(')') + 1)..].TrimStart().StartsWith('Z');
        }
        catch (ArgumentException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private sealed class Crash : IAsyncDisposable
    {
        public string Root { get; } = PhysicalHostLifetimeTests.Root();
        public string Marker => Path.Combine(Root, "child.pid");
        public string Metadata { get; private set; } = "";
        public TaskHostAttempt Record { get; private set; } = null!;
        public Dispatch Dispatch { get; private set; } = null!;
        public int Worker { get; private set; }
        public int Child { get; private set; }
        private readonly Dictionary<int, ulong> processes = [];
        private Process? parent;

        public static async Task<Crash> StartAsync()
        {
            var crash = new Crash();
            try
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                foreach (var value in new[] { typeof(HostReadoptionTests).Assembly.Location, "-class",
                    "Wf.Agent.Tests.HostReadoptionDriver", "-parallel", "none" }) start.ArgumentList.Add(value);
                start.Environment["WF_READOPTION_DRIVER_ROOT"] = crash.Root;
                crash.parent = Process.Start(start) ?? throw new InvalidOperationException("Recovery driver failed to start.");
                crash.Child = await PhysicalHostLifetimeTests.ReadPid(crash.Marker);
                crash.Worker = await PhysicalHostLifetimeTests.ReadPid(crash.Marker + ".worker");
                crash.Metadata = Assert.Single(Directory.GetFiles(Path.Combine(crash.Root, "recovery"),
                    "attempt.json", SearchOption.AllDirectories));
                crash.Record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(crash.Metadata, Token),
                    TaskHostAttemptJson.Default.TaskHostAttempt)!;
                crash.Dispatch = Dispatch.Parser.ParseFrom(Convert.FromBase64String(crash.Record.DispatchBase64!));
                foreach (var pid in new[] { crash.Child, crash.Worker, crash.Record.Pid }.Distinct())
                {
                    using var process = Process.GetProcessById(pid);
                    crash.processes.Add(pid, TaskHostLauncher.StartIdentity(process));
                }
                crash.parent.Kill(entireProcessTree: false);
                Assert.True(crash.parent.WaitForExit(5_000));
                return crash;
            }
            catch { await crash.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            if (parent is { HasExited: false })
            {
                parent.Kill(entireProcessTree: true);
                Assert.True(parent.WaitForExit(5_000));
            }
            foreach (var (pid, identity) in processes)
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited && TaskHostLauncher.StartIdentity(process) == identity)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
                    }
                }
                catch (ArgumentException) { }
                catch (FileNotFoundException) { }
            parent?.Dispose();
            if (Record is not null)
            {
                var socketDirectory = Path.GetDirectoryName(Record.SocketPath)!;
                if (Directory.Exists(socketDirectory))
                {
                    foreach (var suffix in new[] { "", ".lifetime", ".control", ".exited" })
                        File.Delete(Record.SocketPath + suffix);
                    Directory.Delete(socketDirectory);
                }
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}

public sealed class HostReadoptionDriver
{
    [Fact]
    public async Task Agent_style_parent()
    {
        if (Environment.GetEnvironmentVariable("WF_READOPTION_DRIVER_ROOT") is not { } root) return;
        await using var pool = PhysicalHostLifetimeTests.Pool(root);
        var dispatch = PhysicalHostLifetimeTests.Dispatch("readopt", Path.Combine(root, "child.pid"));
        dispatch.ReservationEpoch = 1;
        await pool.ExecuteAsync(PhysicalHostLifetimeTests.Package, dispatch, cancellationToken: TestContext.Current.CancellationToken);
    }
}
