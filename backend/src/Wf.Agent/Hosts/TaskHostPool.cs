using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Wf.Agent.Packages;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Host.V1;

namespace Wf.Agent.Hosts;

public sealed class TaskHostPackage : IDisposable
{
    internal PackageLease? CacheLease { get; init; }
    public string Version { get; }
    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string WorkingDirectory { get; }

    public TaskHostPackage(string version, string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathFullyQualified(workingDirectory))
            throw new ArgumentException("Package working directory must be absolute.", nameof(workingDirectory));
        Version = version;
        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        WorkingDirectory = workingDirectory;
    }

    internal bool Matches(TaskHostPackage other) => Version == other.Version && Executable == other.Executable &&
        WorkingDirectory == other.WorkingDirectory && Arguments.SequenceEqual(other.Arguments);

    public void Dispose() => CacheLease?.Dispose();
}

public sealed class TaskHostPool : IAsyncDisposable
{
    public const int DefaultMaxWarmProcesses = 4;
    public const long DefaultMaxWarmRssBytes = 200_000_000;
    public static readonly TimeSpan DefaultWarmIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);
    private readonly ILogger<TaskHostPool> logger;
    private readonly TimeProvider clock;
    private readonly int maxWarmProcesses;
    private readonly long maxWarmRssBytes;
    private readonly TimeSpan warmIdleTimeout;
    private readonly SemaphoreSlim gate = new(1);
    private readonly List<Host> warm = [];
    private readonly ConcurrentDictionary<int, Host> live = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly ITimer timer;
    private readonly Task maintenance;
    private int processStartCount;
    private bool disposed;

    public TaskHostPool(ILogger<TaskHostPool> logger, TimeProvider? timeProvider = null,
        int maxWarmProcesses = DefaultMaxWarmProcesses, long maxWarmRssBytes = DefaultMaxWarmRssBytes,
        TimeSpan? warmIdleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWarmProcesses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWarmRssBytes);
        var idle = warmIdleTimeout ?? DefaultWarmIdleTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idle, TimeSpan.Zero);
        this.logger = logger;
        clock = timeProvider ?? TimeProvider.System;
        this.maxWarmProcesses = maxWarmProcesses;
        this.maxWarmRssBytes = maxWarmRssBytes;
        this.warmIdleTimeout = idle;
        timer = clock.CreateTimer(static state => ((ChannelWriter<byte>)state!).TryWrite(0), wake.Writer,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        maintenance = MaintainAsync();
    }

    public int ProcessStartCount => Volatile.Read(ref processStartCount);
    public IReadOnlyList<int> WarmProcessIds
    {
        get
        {
            gate.Wait();
            try { return warm.Select(host => host.Process.Id).ToArray(); }
            finally { gate.Release(); }
        }
    }

    public async Task PrewarmAsync(TaskHostPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed || shutdown.IsCancellationRequested, this);
            cancellationToken.ThrowIfCancellationRequested();
            await TrimWarmAsync();
            while (warm.Count >= maxWarmProcesses) await EvictAsync(warm[0]);
            await StartAsync(package, cancellationToken, prewarming: true);
        }
        finally { try { ScheduleSweep(); } finally { gate.Release(); } }
    }

    public async Task<TaskCompleted> ExecuteAsync(TaskHostPackage package, Dispatch dispatch,
        ByteString? nodeOutputsJson = null, TimeSpan? cancellationGrace = null, CancellationToken cancellationToken = default,
        Action<TaskCompleted>? journalResult = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(dispatch);
        var grace = cancellationGrace ?? TimeSpan.FromSeconds(30);
        if (grace < TimeSpan.Zero || grace > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(cancellationGrace));
        if (dispatch.Task is null || dispatch.DeadlineAt is null || dispatch.Version != package.Version)
            throw new ArgumentException("Dispatch must identify an attempt, deadline and matching package version.", nameof(dispatch));
        cancellationToken.ThrowIfCancellationRequested();
        Host host;
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed || shutdown.IsCancellationRequested, this);
            await TrimWarmAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var index = warm.FindIndex(host => host.Package.Matches(package));
            if (index < 0) host = (await StartAsync(package, cancellationToken))!;
            else { host = warm[index]; warm.RemoveAt(index); }
        }
        finally { try { ScheduleSweep(); } finally { gate.Release(); } }
        try
        {
            using var call = host.Client.ExecuteAsync(new ExecuteRequest
            {
                Dispatch = dispatch.Clone(), NodeOutputsJson = nodeOutputsJson ?? dispatch.NodeOutputsJson
            });
            using var deadline = new CancellationTokenSource();
            var remaining = dispatch.DeadlineAt.ToDateTimeOffset() - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) deadline.Cancel();
            else deadline.CancelAfter(remaining);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            TaskCompleted result;
            try { result = await call.ResponseAsync.WaitAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                var timedOut = deadline.IsCancellationRequested;
                using var cancel = host.Client.CancelAsync(new CancelRequest { Task = dispatch.Task, TimedOut = timedOut },
                    deadline: DateTime.UtcNow.AddSeconds(3));
                try { await cancel.ResponseAsync; }
                catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.NotFound)
                {
                    logger.LogWarning("Task host {Pid} did not accept cancellation: {Status}.", host.Process.Id, exception.StatusCode);
                }
                try { result = await call.ResponseAsync.WaitAsync(grace); }
                catch (TimeoutException)
                {
                    logger.LogWarning("Task host {Pid} ignored cancellation; killing its process tree.", host.Process.Id);
                    return new TaskCompleted
                    {
                        Task = dispatch.Task.Clone(), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        Outcome = timedOut ? TaskOutcome.TimedOut : TaskOutcome.Cancelled,
                        Error = new TaskError
                        {
                            Category = timedOut ? "Timeout" : "Aborted", NonRetryable = !timedOut,
                            Message = $"Task host ignored cancellation for {grace}; its process tree was terminated."
                        }
                    };
                }
            }
            if (result.Task is null || !result.Task.Equals(dispatch.Task) ||
                result.Outcome == TaskOutcome.Unspecified || !System.Enum.IsDefined(result.Outcome))
                throw new InvalidDataException("Task host returned an invalid attempt result.");
            journalResult?.Invoke(result);
            using var acknowledgement = host.Client.AcknowledgeAsync(new AcknowledgeRequest { Task = dispatch.Task },
                deadline: DateTime.UtcNow.AddSeconds(5));
            await acknowledgement.ResponseAsync;
            await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (host.Process.ExitCode != 0)
                throw new InvalidOperationException($"Task host exited with code {host.Process.ExitCode}: {host.Diagnostics}");
            return result;
        }
        finally { await ReleaseAsync(host); }
    }

    private async Task<Host?> StartAsync(TaskHostPackage package, CancellationToken cancellationToken, bool prewarming = false)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        cancellationToken = lifetime.Token;
        using var startingLease = package.CacheLease?.Retain();
        var directory = Path.Combine(Path.GetTempPath(), $"wf-h-{Guid.NewGuid():N}");
        CreatePrivateDirectory(directory);
        var socketPath = Path.Combine(directory, "s");
        var info = new ProcessStartInfo(package.Executable)
        {
            WorkingDirectory = package.WorkingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in package.Arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--task-host");
        info.ArgumentList.Add("--socket");
        info.ArgumentList.Add(socketPath);
        var process = new Process { StartInfo = info };
        var started = false;
        PackageLease? processLease = null;
        Host? host = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            processLease = startingLease?.Retain();
            // Exit owns the final safety pin even if readiness or process cleanup fails.
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => processLease?.Dispose();
            if (!process.Start()) throw new InvalidOperationException("Task-host process did not start.");
            started = true;
            Interlocked.Increment(ref processStartCount);
            var endpoint = new UnixDomainSocketEndPoint(socketPath);
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(endpoint, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler, DisposeHttpClient = true,
                MaxReceiveMessageSize = 1024 * 1024, MaxSendMessageSize = 1024 * 1024
            });
            host = new Host(package, directory, socketPath, process, channel, logger, processLease);
            live.TryAdd(process.Id, host);
            if (prewarming)
            {
                host.IdleSince = clock.GetTimestamp();
                warm.Add(host);
            }
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                readiness.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    await host.Output;
                    await host.Error;
                    throw new InvalidOperationException($"Task host exited before readiness with code {process.ExitCode}: {host.Diagnostics}");
                }
                await TrimWarmAsync(prewarming ? host : null);
                if (prewarming && !warm.Contains(host)) return null;
                try
                {
                    using var call = host.Client.ReadyAsync(new ReadyRequest(), deadline: DateTime.UtcNow.AddSeconds(1),
                        cancellationToken: readiness.Token);
                    // The gate serializes starts; sample while the handshake is pending as well.
                    while (!call.ResponseAsync.IsCompleted)
                    {
                        await Task.WhenAny(call.ResponseAsync, Task.Delay(MaintenanceInterval, readiness.Token));
                        readiness.Token.ThrowIfCancellationRequested();
                        await TrimWarmAsync(prewarming ? host : null);
                        if (prewarming && !warm.Contains(host)) return null;
                    }
                    var ready = await call.ResponseAsync;
                    if (ready.ProtocolVersion != 1 || ready.ProcessId != process.Id || ready.WorkflowVersion != package.Version || ready.Bound)
                        throw new InvalidDataException("Task-host readiness identity, version or state is invalid.");
                    await TrimWarmAsync(prewarming ? host : null);
                    if (prewarming && !warm.Contains(host)) return null;
                    host.IdleSince = clock.GetTimestamp();
                    host.Ready = true;
                    return host;
                }
                catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
                {
                    logger.LogDebug("Waiting for task host {Pid}: {Status}.", process.Id, exception.StatusCode);
                    await Task.Delay(25, readiness.Token);
                }
            }
        }
        catch (Exception exception)
        {
            if (!started) processLease?.Dispose();
            if (host is not null) await EvictAsync(host);
            else
            {
                if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
                if (File.Exists(socketPath)) File.Delete(socketPath);
                Directory.Delete(directory);
            }
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Task-host startup was cancelled.", exception, cancellationToken);
            if (exception is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
                throw new TimeoutException($"Task host did not become ready within 15 seconds: {host?.Diagnostics}", exception);
            throw;
        }
    }

    private Task ReleaseAsync(Host host)
    {
        lock (host)
        {
            if (host.Release is null || host.Release.IsFaulted) host.Release = ReleaseCoreAsync(host);
            return host.Release;
        }
    }

    private async Task ReleaseCoreAsync(Host host)
    {
        host.Channel.Dispose();
        try
        {
            if (!host.Process.HasExited) host.Process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or InvalidOperationException) && host.Process.HasExited) { }
        await host.Process.WaitForExitAsync();
        host.PackageLease?.Dispose();
        await Task.WhenAll(host.Output, host.Error);
        if (File.Exists(host.SocketPath)) File.Delete(host.SocketPath);
        if (Directory.Exists(host.Directory)) Directory.Delete(host.Directory);
        host.Process.Dispose();
        live.TryRemove(host.Id, out _);
    }

    private async Task EvictAsync(Host host)
    {
        await ReleaseAsync(host);
        warm.Remove(host);
    }

    private async Task TrimWarmAsync(Host? starting = null)
    {
        long rss = 0;
        var sizes = new Dictionary<Host, long>();
        foreach (var host in warm.ToArray())
        {
            host.Process.Refresh();
            var exited = host.Process.HasExited;
            var exitCode = exited ? host.Process.ExitCode : 0;
            if (exited || (!host.Ready && host != starting) || clock.GetElapsedTime(host.IdleSince) >= warmIdleTimeout)
            {
                await EvictAsync(host);
                if (exited && host == starting)
                    throw new InvalidOperationException($"Task host exited before readiness with code {exitCode}: {host.Diagnostics}");
                continue;
            }
            long bytes;
            try { bytes = host.Process.WorkingSet64; }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(exception, "Cannot sample warm task host {Pid}; evicting it.", host.Id);
                await EvictAsync(host);
                continue;
            }
            sizes.Add(host, bytes);
            rss += bytes;
        }
        // List order is global LRU: every host is used at most once, then exits.
        while (warm.Count > maxWarmProcesses || rss > maxWarmRssBytes)
        {
            var oldest = warm[0];
            rss -= sizes[oldest];
            await EvictAsync(oldest);
        }
    }

    private async Task MaintainAsync()
    {
        try
        {
            while (await wake.Reader.WaitToReadAsync(shutdown.Token))
            {
                wake.Reader.TryRead(out _);
                await gate.WaitAsync(shutdown.Token);
                try { await TrimWarmAsync(); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Warm task-host cleanup failed; admission remains subject to the global budgets.");
                }
                finally { try { ScheduleSweep(); } finally { gate.Release(); } }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private void ScheduleSweep()
    {
        if (shutdown.IsCancellationRequested || disposed) return;
        var delay = warm.Count == 0 ? Timeout.InfiniteTimeSpan : MaintenanceInterval;
        foreach (var host in warm)
        {
            var remaining = warmIdleTimeout - clock.GetElapsedTime(host.IdleSince);
            if (remaining > TimeSpan.Zero && remaining < delay) delay = remaining;
        }
        if (delay > TimeSpan.Zero && delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
        timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Agent has no Windows user identity.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        await maintenance;
        await gate.WaitAsync();
        try
        {
            if (disposed && live.IsEmpty) return;
            disposed = true;
            timer.Dispose();
            warm.Clear();
            await Task.WhenAll(live.Values.Select(ReleaseAsync));
        }
        finally { gate.Release(); }
    }

    private sealed class Host
    {
        private readonly ConcurrentQueue<string> diagnostics = new();
        public TaskHostPackage Package { get; }
        public string Directory { get; }
        public string SocketPath { get; }
        public Process Process { get; }
        public GrpcChannel Channel { get; }
        public TaskHost.TaskHostClient Client { get; }
        public Task Output { get; }
        public Task Error { get; }
        public PackageLease? PackageLease { get; }
        public int Id { get; }
        public long IdleSince { get; set; }
        public bool Ready { get; set; }
        public Task? Release { get; set; }
        public string Diagnostics => string.Join(Environment.NewLine, diagnostics);

        public Host(TaskHostPackage package, string directory, string socketPath, Process process, GrpcChannel channel,
            ILogger<TaskHostPool> logger, PackageLease? packageLease)
        {
            PackageLease = packageLease;
            Id = process.Id;
            Package = package; Directory = directory; SocketPath = socketPath; Process = process; Channel = channel;
            Client = new(channel);
            Output = DrainAsync(process.StandardOutput);
            Error = DrainAsync(process.StandardError);

            async Task DrainAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    diagnostics.Enqueue(line.Length > 4096 ? line[..4096] : line);
                    while (diagnostics.Count > 20) diagnostics.TryDequeue(out _);
                    logger.LogInformation("Task host {Pid}: {Line}", process.Id, line);
                }
            }
        }
    }
}
