using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Wf.Agent.Packages;
using Wf.Agent.Secrets;
using Wf.Agent.Sampling;
using Wf.Agent.Execution;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Host.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Checkpoints;
using Wf.Agent.Limits;

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

public sealed partial class TaskHostPool : IAsyncDisposable
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
    private readonly ConcurrentDictionary<AttemptRef, Host> recovered = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly ITimer timer;
    private readonly Task maintenance;
    private readonly ResourceSamplingService? sampling;
    private readonly ISecretStore? secretStore;
    private readonly HostRecoveryBarrier recovery;
    private readonly string? linuxCgroupRoot;
    private Exception? containmentFailure;
    private int processStartCount;
    private bool disposed;

    public TaskHostPool(ILogger<TaskHostPool> logger, TimeProvider? timeProvider = null,
        int maxWarmProcesses = DefaultMaxWarmProcesses, long maxWarmRssBytes = DefaultMaxWarmRssBytes,
        TimeSpan? warmIdleTimeout = null, ResourceSamplingService? sampling = null, ISecretStore? secretStore = null,
        string? containmentDirectory = null, AgentOptions? options = null)
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
        this.sampling = sampling;
        this.secretStore = secretStore;
        linuxCgroupRoot = options?.LinuxCgroupRoot;
        recovery = new HostRecoveryBarrier(containmentDirectory ?? (options is null ? null :
            Path.Combine(options.ResultJournalDirectory, "host-lifetimes", Guid.Parse(options.Id).ToString("N"))));
        timer = clock.CreateTimer(static state => ((ChannelWriter<byte>)state!).TryWrite(0), wake.Writer,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        maintenance = MaintainAsync();
    }

    public int ProcessStartCount => Volatile.Read(ref processStartCount);

    /// <summary>
    /// Stops every host in this pool, not only a particular reservation epoch. Callers must block admission
    /// and reject stale fences before calling, then drain execution tasks before acknowledging the fence.
    /// Unproven same-boot lifetimes quarantine this identity until authenticated recovery or a machine reboot.
    /// </summary>
    public async Task FenceAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (containmentFailure is not null)
                throw new InvalidOperationException("Physical task-host cleanup failed; reservation fencing is quarantined.", containmentFailure);
            await Task.WhenAll(live.Values.Select(ReleaseAsync)).WaitAsync(cancellationToken);
            warm.Clear();
            recovery.AssertSafe(allowActive: false);
        }
        finally { gate.Release(); }
    }
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
        Action<TaskCompleted>? journalResult = null, Func<string, string, Task>? captureLog = null,
        SecretMasker? secretMasker = null, Action<AgentMessage>? publishResources = null,
        Func<CheckpointSaved, CancellationToken, Task>? publishCheckpoint = null,
        Action<TaskProgress>? publishProgress = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(dispatch);
        var grace = cancellationGrace ?? TimeSpan.FromSeconds(30);
        if (grace < TimeSpan.Zero || grace > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(cancellationGrace));
        if (dispatch.Task is null || dispatch.DeadlineAt is null || dispatch.Version != package.Version)
            throw new ArgumentException("Dispatch must identify an attempt, deadline and matching package version.", nameof(dispatch));
        var limits = dispatch.Limits is { } requested ? new TaskResourceLimits(requested.CpuCores, requested.RamMb) : null;
        cancellationToken.ThrowIfCancellationRequested();
        Host host;
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed || shutdown.IsCancellationRequested, this);
            await TrimWarmAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (recovered.TryRemove(dispatch.Task, out var adopted))
            {
                host = adopted;
                if (!host.Package.Matches(package))
                    throw new InvalidDataException("Recovered host package does not match the dispatched version.");
            }
            else
            {
                var index = warm.FindIndex(host => host.Package.Matches(package));
                if (index < 0) host = (await StartAsync(package, cancellationToken))!;
                else { host = warm[index]; warm.RemoveAt(index); }
            }
        }
        finally { try { ScheduleSweep(); } finally { gate.Release(); } }
        using var limitStop = new CancellationTokenSource();
        using var memoryBreach = new CancellationTokenSource();
        Task limitMonitor = Task.CompletedTask;
        try
        {
            lock (host)
            {
                if (host.Release is not null)
                    throw new InvalidOperationException("Task host was fenced before its attempt could bind.");
                if (!host.Adopted)
                    host.Containment.Record(host.Process, host.Package, host.SocketPath,
                        host.WorkloadLimiter?.LinuxCgroupPath, dispatch);
            }
            if (limits is { } && (limits.CpuCores > 0 || limits.RamMb > 0))
            {
                lock (host)
                {
                    if (host.Release is not null)
                        throw new InvalidOperationException("Task host was fenced before resource limits could bind.");
                    host.Limiter ??= OperatingSystem.IsWindows() ? new JobObjectLimiter(host.Process, limits, host.Containment.ResourceJobName) :
                        OperatingSystem.IsLinux() ? host.WorkloadLimiter ??
                            throw new InvalidOperationException("Task resource limits require Agent:LinuxCgroupRoot delegation before worker startup.") :
                        throw new PlatformNotSupportedException("Task resource limits require Windows Jobs or Linux cgroups v2.");
                    host.LimitStop = limitStop;
                    host.LimitMonitor = limitMonitor = MonitorLimitsAsync();
                    if (!host.Adopted) host.WorkloadLimiter?.Apply(limits);
                }
                if (!host.Adopted)
                {
                    var bindingRequest = OperatingSystem.IsWindows()
                        ? new ApplyLimitsRequest { WindowsJobName = host.Containment.ResourceJobName }
                        : new ApplyLimitsRequest { LinuxCgroupPath = host.Limiter.LinuxCgroupPath! };
                    using var binding = host.Client.ApplyLimitsAsync(bindingRequest,
                        deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
                    await binding.ResponseAsync;
                }
            }
            using var resourceTracking = sampling?.Track(host.Process, dispatch.Task, host.ResourceScope,
                host.ResourceUnavailableReason, publishResources is null ? null : message =>
                {
                    host.Containment.RecordResourceSample(message.ResourceSample);
                    publishResources(message);
                }, dispatch.ReservationEpoch, host.ResourceSequence);
            host.CaptureLog = captureLog;
            var request = new ExecuteRequest { Dispatch = dispatch.Clone(), NodeOutputsJson = nodeOutputsJson ?? dispatch.NodeOutputsJson };
            using var deadline = new CancellationTokenSource();
            var remaining = dispatch.DeadlineAt.ToDateTimeOffset() - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) deadline.Cancel();
            else deadline.CancelAfter(remaining);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken, memoryBreach.Token);
            try
            {
                using var required = host.Client.RequiredSecretsAsync(new ExecuteRequest { Dispatch = dispatch.Clone() },
                    deadline: host.Adopted ? DateTime.UtcNow.AddSeconds(3) : null,
                    cancellationToken: host.Adopted ? cancellationToken : stop.Token);
                var requiredSecrets = await required.ResponseAsync;
                var references = requiredSecrets.Names;
                if (references.Count > 128 || references.Any(name => !SecretReference.IsValidName(name)))
                    throw new SecretResolutionException();
                if (host.Adopted && requiredSecrets.RecoveredValues.Count != references.Count)
                    throw new SecretResolutionException();
                var bytes = 0;
                foreach (var name in references.Distinct(StringComparer.Ordinal))
                {
                    var reference = new SecretReference(name);
                    var value = host.Adopted
                        ? requiredSecrets.RecoveredValues.TryGetValue(name, out var original) ? original : null
                        : secretStore is not null ? await secretStore.ResolveAsync(reference, stop.Token)
                        : throw new SecretResolutionException(reference);
                    if (string.IsNullOrEmpty(value) || value.Contains('\0') ||
                        Encoding.UTF8.GetByteCount(value) > LocalSecretStore.MaxSecretBytes)
                        throw new SecretResolutionException(reference);
                    bytes = checked(bytes + Encoding.UTF8.GetByteCount(value));
                    if (bytes > 128 * 1024) throw new SecretResolutionException();
                    request.ResolvedSecrets.Add(name, value);
                }
            }
            catch (SecretResolutionException error)
            {
                host.Masker = (secretMasker ?? new SecretMasker([])).WithValues(request.ResolvedSecrets.Values);
                return SecretFailure(host.Masker.Redact(error.Message));
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument &&
                error.Status.Detail == "Task secret references are invalid.")
            { return SecretFailure("Task secret references are invalid."); }
            catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && stop.IsCancellationRequested)
            {
                if (memoryBreach.IsCancellationRequested) throw new ResourceLimitException();
                cancellationToken.ThrowIfCancellationRequested();
                return SecretFailure("Task secret resolution exceeded the attempt deadline.", timedOut: true);
            }
            catch (OperationCanceledException) when (memoryBreach.IsCancellationRequested)
            { throw new ResourceLimitException(); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { return SecretFailure("Task secret resolution exceeded the attempt deadline.", timedOut: true); }
            // Keep manually supplied maskers for existing callers, while always masking real resolved values.
            host.Masker = (secretMasker ?? new SecretMasker([])).WithValues(request.ResolvedSecrets.Values);
            host.HasSecrets = request.ResolvedSecrets.Count != 0;
            if (host.Adopted) request.ResolvedSecrets.Clear();
            using var checkpointStop = new CancellationTokenSource();
            using var checkpointStream = host.Client.Checkpoints(new ReadyRequest(), cancellationToken: checkpointStop.Token);
            using var progressStream = host.Client.Progress(new ReadyRequest(), cancellationToken: checkpointStop.Token);
            var forwarding = ForwardCheckpoints();
            var progressForwarding = ForwardProgress();
            using var call = host.Adopted ? host.Client.ResumeAsync(request) : host.Client.ExecuteAsync(request);
            TaskCompleted result;
            try
            {
                var first = await (host.Limiter is null
                    ? Task.WhenAny(call.ResponseAsync, forwarding, progressForwarding)
                    : Task.WhenAny(call.ResponseAsync, forwarding, progressForwarding, limitMonitor)).WaitAsync(stop.Token);
                if (host.Limiter is not null && first == limitMonitor)
                {
                    await limitMonitor;
                    if (memoryBreach.IsCancellationRequested) throw new ResourceLimitException();
                    throw new InvalidOperationException("Task-host resource monitor stopped before execution completed.");
                }
                if (first != call.ResponseAsync) await first;
                result = await call.ResponseAsync.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                if (memoryBreach.IsCancellationRequested) throw new ResourceLimitException();
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
                    await checkpointStop.CancelAsync();
                    await Task.WhenAll(forwarding, progressForwarding);
                    await host.Containment.StopAsync(host.Process);
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
            finally
            {
                await checkpointStop.CancelAsync();
                await Task.WhenAll(forwarding, progressForwarding);
            }
            if (result.Task is null || !result.Task.Equals(dispatch.Task) ||
                result.Outcome == TaskOutcome.Unspecified || !System.Enum.IsDefined(result.Outcome))
                throw new InvalidDataException("Task host returned an invalid attempt result.");
            if (host.Limiter?.MemoryLimitExceeded == true || limits?.RamMb > 0 && result.Error?.Category == "ResourceLimit")
                throw new ResourceLimitException();
            resourceTracking?.Dispose();
            result = OutputsGuard.Apply(result, host.Masker);
            if (host.Limiter is null) journalResult?.Invoke(result);
            using var acknowledgement = host.Client.AcknowledgeAsync(new AcknowledgeRequest { Task = dispatch.Task },
                deadline: DateTime.UtcNow.AddSeconds(5));
            await acknowledgement.ResponseAsync;
            await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (!host.Adopted && host.Process.ExitCode != 0)
                throw new InvalidOperationException($"Task host exited with code {host.Process.ExitCode}: {host.Diagnostics}");
            if (host.Limiter is not null)
            {
                if (host.Limiter.MemoryLimitExceeded) throw new ResourceLimitException();
                await StopLimitMonitorAsync();
                await ReleaseAsync(host);
                journalResult?.Invoke(result);
            }
            return result;

            async Task ForwardCheckpoints()
            {
                try
                {
                    await foreach (var saved in checkpointStream.ResponseStream.ReadAllAsync(checkpointStop.Token))
                    {
                        _ = CheckpointProtocol.Validate(saved);
                        if (!saved.Task.Equals(dispatch.Task) || saved.ReservationEpoch != dispatch.ReservationEpoch)
                            throw new InvalidDataException("Task host checkpoint does not match its dispatched attempt.");
                        if (publishCheckpoint is null)
                            throw new InvalidOperationException("Durable checkpoint acknowledgement is not configured at the agent.");
                        await publishCheckpoint(saved, checkpointStop.Token);
                        using var confirmed = host.Client.ConfirmCheckpointAsync(saved, cancellationToken: checkpointStop.Token);
                        await confirmed.ResponseAsync;
                    }
                }
                catch (OperationCanceledException) when (checkpointStop.IsCancellationRequested) { }
                catch (RpcException error) when (checkpointStop.IsCancellationRequested && error.StatusCode == StatusCode.Cancelled) { }
            }

            async Task ForwardProgress()
            {
                try
                {
                    await foreach (var update in progressStream.ResponseStream.ReadAllAsync(checkpointStop.Token))
                    {
                        _ = Wf.Contracts.Progress.TaskProgressProtocol.Validate(update);
                        if (!update.Task.Equals(dispatch.Task) || update.ReservationEpoch != dispatch.ReservationEpoch)
                            throw new InvalidDataException("Task host progress does not match its dispatched attempt.");
                        if (publishProgress is null)
                            throw new InvalidOperationException("Durable task progress journaling is not configured at the agent.");
                        publishProgress(update);
                        using var confirmed = host.Client.ConfirmProgressAsync(update, cancellationToken: checkpointStop.Token);
                        await confirmed.ResponseAsync;
                    }
                }
                catch (OperationCanceledException) when (checkpointStop.IsCancellationRequested) { }
                catch (RpcException error) when (checkpointStop.IsCancellationRequested && error.StatusCode == StatusCode.Cancelled) { }
            }
        }
        catch (Exception error) when (error is ResourceLimitException ||
            error is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.Unknown or
                StatusCode.Internal or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted } &&
            host.Limiter?.MemoryLimitExceeded == true)
        {
            await StopLimitMonitorAsync();
            await ReleaseAsync(host);
            var result = new TaskCompleted
            {
                Task = dispatch.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()),
                Outcome = TaskOutcome.Failed,
                Error = new TaskError { Category = "ResourceLimit", NonRetryable = true,
                    Message = $"Task exceeded its {limits!.RamMb} MiB RAM limit; its process tree was terminated." }
            };
            journalResult?.Invoke(result);
            return result;
        }
        catch (Exception error) when (host.HasSecrets && error is RpcException or InvalidOperationException or IOException or TimeoutException)
        { throw new InvalidOperationException(host.Masker.Redact(error.Message)); }
        finally
        {
            try { await StopLimitMonitorAsync(); }
            finally { await ReleaseAsync(host); }
        }

        async Task StopLimitMonitorAsync()
        {
            await limitStop.CancelAsync();
            await limitMonitor;
        }

        async Task MonitorLimitsAsync()
        {
            try
            {
                while (!limitStop.IsCancellationRequested)
                {
                    if (host.Limiter!.MemoryLimitExceeded)
                    {
                        await memoryBreach.CancelAsync();
                        return;
                    }
                    await Task.Delay(25, limitStop.Token);
                }
            }
            catch (OperationCanceledException) when (limitStop.IsCancellationRequested) { }
        }

        TaskCompleted SecretFailure(string message, bool timedOut = false) => new()
        {
            Task = dispatch.Task.Clone(), At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()),
            Outcome = timedOut ? TaskOutcome.TimedOut : TaskOutcome.Failed,
            Error = new TaskError { Category = timedOut ? "Timeout" : "ValidationError", NonRetryable = !timedOut, Message = message }
        };
    }

    private async Task<Host?> StartAsync(TaskHostPackage package, CancellationToken cancellationToken, bool prewarming = false)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        cancellationToken = lifetime.Token;
        using var startingLease = package.CacheLease?.Retain();
        if (containmentFailure is not null)
            throw new InvalidOperationException("Physical task-host cleanup failed; new work is quarantined.", containmentFailure);
        recovery.AssertSafe();
        var directory = Path.Combine(Path.GetTempPath(), $"wf-h-{Guid.NewGuid():N}");
        CreatePrivateDirectory(directory);
        var socketPath = Path.Combine(directory, "s");
        var info = new ProcessStartInfo(package.Executable)
        {
            WorkingDirectory = package.WorkingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in package.Arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--task-host");
        info.ArgumentList.Add("--socket");
        info.ArgumentList.Add(socketPath);
        string? resourceUnavailable = null;
        var resourceScope = sampling?.CreateProcessScope(out resourceUnavailable);
        try { resourceScope?.PrepareStart(info); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or ArgumentException)
        {
            resourceUnavailable = exception.Message;
            DisposeResourceScope(resourceScope);
            resourceScope = null;
        }
        var process = new Process { StartInfo = info };
        var containment = new TaskHostContainment(recovery, info, socketPath);
        CgroupV2Limiter? workloadLimiter = null;
        var started = false;
        PackageLease? processLease = null;
        Host? host = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            info.Environment.Remove("WF_TASK_HOST_WORKLOAD_CGROUP");
            if (OperatingSystem.IsLinux() &&
                ((resourceScope as LinuxTaskResourceScope)?.DirectoryPath ?? linuxCgroupRoot) is { } delegatedRoot)
            {
                try
                {
                    workloadLimiter = new CgroupV2Limiter(delegatedRoot);
                    info.Environment["WF_TASK_HOST_WORKLOAD_CGROUP"] = workloadLimiter.LinuxCgroupPath;
                }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or
                    UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    logger.LogWarning("Task host cannot prepare workload resource limits: {Reason}", error.Message);
                }
            }
            if (resourceScope is null) sampling?.PrepareUnscopedStart(info);
            processLease = startingLease?.Retain();
            // Exit owns the final safety pin even if readiness or process cleanup fails.
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => processLease?.Dispose();
            if (!process.Start()) throw new InvalidOperationException("Task-host process did not start.");
            started = true;
            containment.Bind(process);
            containment.Record(process, package, socketPath, workloadLimiter?.LinuxCgroupPath);
            Interlocked.Increment(ref processStartCount);
            var resourceBound = resourceScope is not null;
            try { resourceScope?.Bind(process); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                resourceBound = false;
                resourceUnavailable = exception.Message;
                logger.LogWarning("Task host native resource scope unavailable: {Reason}", resourceUnavailable);
            }
            var channel = CreateChannel(socketPath, containment.Token);
            host = new Host(package, directory, socketPath, process, channel, logger, processLease,
                resourceScope, resourceBound, resourceUnavailable, containment, workloadLimiter);
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
                    if (ready.ProtocolVersion != 8 || ready.ProcessId != process.Id || ready.WorkflowVersion != package.Version || ready.Bound)
                        throw new InvalidDataException("Task-host readiness identity, version or state is invalid.");
                    containment.VerifyReady();
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
                try
                {
                    if (started) await containment.StopAsync(process);
                    containment.Complete();
                }
                catch (Exception cleanupError)
                {
                    containmentFailure = cleanupError;
                    containment.Dispose();
                    throw;
                }
                process.Dispose();
                workloadLimiter?.Dispose();
                DisposeResourceScope(resourceScope);
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

    private static GrpcChannel CreateChannel(string socketPath, string token)
    {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("x-wf-host-token", token);
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpClient = http, DisposeHttpClient = true,
            MaxReceiveMessageSize = 1024 * 1024, MaxSendMessageSize = 4 * 1024 * 1024
        });
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
            try
            {
                if (host.LimitStop is not null) await host.LimitStop.CancelAsync();
                await host.LimitMonitor;
            }
            finally
            {
                await host.Containment.StopAsync(host.Process);
                host.Containment.Complete();
            }
        }
        catch (Exception exception)
        {
            containmentFailure = exception;
            host.Containment.Dispose();
            throw;
        }
        host.PackageLease?.Dispose();
        try { await Task.WhenAll(host.Output, host.Error); }
        finally
        {
            try
            {
                if (File.Exists(host.SocketPath)) File.Delete(host.SocketPath);
                if (Directory.Exists(host.Directory)) Directory.Delete(host.Directory);
            }
            finally
            {
                try { host.Limiter?.Dispose(); host.WorkloadLimiter?.Dispose(); }
                finally
                {
                    host.Process.Dispose();
                    DisposeResourceScope(host.CleanupResourceScope);
                    foreach (var pair in recovered.Where(pair => ReferenceEquals(pair.Value, host)))
                        recovered.TryRemove(pair.Key, out _);
                    live.TryRemove(host.Id, out _);
                }
            }
        }
    }

    private void DisposeResourceScope(ITaskResourceScope? scope)
    {
        try { scope?.Dispose(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Owned task resource scope cleanup failed: {Reason}", exception.Message);
        }
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
            try { bytes = WarmWorkingSet(host.Process); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
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

    private static long WarmWorkingSet(Process root)
    {
        if (!OperatingSystem.IsLinux()) return root.WorkingSet64;
        long bytes = 0;
        var pending = new Stack<int>();
        var seen = new HashSet<int>();
        pending.Push(root.Id);
        while (pending.TryPop(out var pid))
        {
            if (!seen.Add(pid)) continue;
            using var process = Process.GetProcessById(pid);
            bytes = checked(bytes + process.WorkingSet64);
            foreach (var thread in Directory.EnumerateDirectories($"/proc/{pid}/task"))
                foreach (var child in File.ReadAllText(Path.Combine(thread, "children"))
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    pending.Push(int.Parse(child, System.Globalization.CultureInfo.InvariantCulture));
        }
        return bytes;
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

    internal static void CreatePrivateDirectory(string path)
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

    private sealed class ResourceLimitException : Exception;

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
        public ITaskResourceScope? ResourceScope { get; }
        public ITaskResourceScope? CleanupResourceScope { get; }
        public TaskHostContainment Containment { get; }
        public IResourceLimiter? Limiter { get; set; }
        public CgroupV2Limiter? WorkloadLimiter { get; }
        public CancellationTokenSource? LimitStop { get; set; }
        public Task LimitMonitor { get; set; } = Task.CompletedTask;
        public string? ResourceUnavailableReason { get; }
        public int Id { get; }
        public long IdleSince { get; set; }
        public bool Ready { get; set; }
        public Task? Release { get; set; }
        public Func<string, string, Task>? CaptureLog { get; set; }
        public SecretMasker Masker { get; set; } = new([]);
        public bool HasSecrets { get; set; }
        public bool Adopted { get; }
        public ulong ResourceSequence { get; init; }
        public string Diagnostics => string.Join(Environment.NewLine, diagnostics);

        public Host(TaskHostPackage package, string directory, string socketPath, Process process, GrpcChannel channel,
            ILogger<TaskHostPool> logger, PackageLease? packageLease, ITaskResourceScope? resourceScope,
            bool resourceBound, string? resourceUnavailableReason, TaskHostContainment containment, CgroupV2Limiter? workloadLimiter,
            bool adopted = false)
        {
            Adopted = adopted;
            WorkloadLimiter = workloadLimiter;
            Containment = containment;
            CleanupResourceScope = resourceScope;
            ResourceScope = resourceBound ? resourceScope : null;
            ResourceUnavailableReason = resourceUnavailableReason;
            PackageLease = packageLease;
            Id = process.Id;
            Package = package; Directory = directory; SocketPath = socketPath; Process = process; Channel = channel;
            Client = new(channel);
            Output = adopted ? Task.CompletedTask : DrainAsync(process.StandardOutput, "stdout");
            Error = adopted ? Task.CompletedTask : DrainAsync(process.StandardError, "stderr");

            async Task DrainAsync(StreamReader reader, string stream)
            {
                SecretMasker? currentMasker = null;
                SecretMasker.TextStream? masking = null;
                var line = new StringBuilder();
                var fragmented = false;
                var buffer = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    if (!ReferenceEquals(currentMasker, Masker))
                    {
                        currentMasker = Masker;
                        masking = currentMasker.CreateStream();
                    }
                    await ConsumeAsync(masking!.Append(new string(buffer, 0, read)));
                }
                if (masking is not null) await ConsumeAsync(masking.Complete());
                if (line.Length > 0) await PublishAsync(line.ToString());

                async Task ConsumeAsync(string text)
                {
                    foreach (var character in text)
                    {
                        if (character == '\n')
                        {
                            var value = line.ToString();
                            if (value.EndsWith('\r')) value = value[..^1];
                            if (value.Length > 0 || !fragmented) await PublishAsync(value);
                            line.Clear();
                            fragmented = false;
                        }
                        else line.Append(character);
                        // A malicious task cannot make the agent buffer an unbounded newline-free string.
                        if (line.Length >= 16 * 1024)
                        {
                            var count = line.Length;
                            if (char.IsHighSurrogate(line[^1]) || line[^1] == '\r') count--;
                            if (count >= 16 * 1024)
                            {
                                await PublishAsync(line.ToString(0, count));
                                line.Remove(0, count);
                                fragmented = true;
                            }

                        }
                    }
                }

                async Task PublishAsync(string text)
                {
                    diagnostics.Enqueue(text.Length > 4096 ? text[..4096] : text);
                    while (diagnostics.Count > 20) diagnostics.TryDequeue(out _);
                    if (CaptureLog is { } capture) await capture(stream, text);
                    else logger.LogInformation("Task host {Pid}: {Line}", process.Id, text);
                }
            }
        }
    }
}
