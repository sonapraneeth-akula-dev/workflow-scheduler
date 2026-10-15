using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
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

public sealed class TaskHostPool(ILogger<TaskHostPool> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly List<Host> warm = [];
    private readonly ConcurrentDictionary<int, Host> live = new();
    private int processStartCount;
    private bool disposed;
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
            ObjectDisposedException.ThrowIf(disposed, this);
            if (warm.Count >= 4)
                throw new InvalidOperationException("The warm task-host pool already contains four processes.");
            warm.Add(await StartAsync(package, cancellationToken));
        }
        finally { gate.Release(); }
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
            ObjectDisposedException.ThrowIf(disposed, this);
            var index = warm.FindIndex(host => host.Package.Matches(package));
            if (index < 0) host = await StartAsync(package, cancellationToken);
            else { host = warm[index]; warm.RemoveAt(index); }
        }
        finally { gate.Release(); }
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

    private async Task<Host> StartAsync(TaskHostPackage package, CancellationToken cancellationToken)
    {
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
        Host? host = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            host = new Host(package, directory, socketPath, process, channel, logger, startingLease?.Retain());
            live.TryAdd(process.Id, host);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                if (process.HasExited)
                {
                    await host.Output;
                    await host.Error;
                    throw new InvalidOperationException($"Task host exited before readiness with code {process.ExitCode}: {host.Diagnostics}");
                }
                try
                {
                    using var call = host.Client.ReadyAsync(new ReadyRequest(), deadline: DateTime.UtcNow.AddSeconds(1),
                        cancellationToken: readiness.Token);
                    var ready = await call.ResponseAsync;
                    if (ready.ProtocolVersion != 1 || ready.ProcessId != process.Id || ready.WorkflowVersion != package.Version || ready.Bound)
                        throw new InvalidDataException("Task-host readiness identity, version or state is invalid.");
                    return host;
                }
                catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
                {
                    logger.LogDebug("Waiting for task host {Pid}: {Status}.", process.Id, exception.StatusCode);
                    await Task.Delay(25, readiness.Token);
                }
            }
        }
        catch
        {
            if (host is not null) await ReleaseAsync(host);
            else
            {
                if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
                if (File.Exists(socketPath)) File.Delete(socketPath);
                Directory.Delete(directory);
            }
            throw;
        }
    }

    private async Task ReleaseAsync(Host host)
    {
        if (!live.TryRemove(host.Process.Id, out _)) return;
        host.Channel.Dispose();
        try
        {
            if (!host.Process.HasExited) host.Process.Kill(entireProcessTree: true);
            await host.Process.WaitForExitAsync();
            host.PackageLease?.Dispose();
            await Task.WhenAll(host.Output, host.Error);
        }
        finally
        {
            host.Process.Dispose();
            if (File.Exists(host.SocketPath)) File.Delete(host.SocketPath);
            Directory.Delete(host.Directory);
        }
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
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            warm.Clear();
            foreach (var host in live.Values) await ReleaseAsync(host);
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
        public string Diagnostics => string.Join(Environment.NewLine, diagnostics);

        public Host(TaskHostPackage package, string directory, string socketPath, Process process, GrpcChannel channel,
            ILogger<TaskHostPool> logger, PackageLease? packageLease)
        {
            PackageLease = packageLease;
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
