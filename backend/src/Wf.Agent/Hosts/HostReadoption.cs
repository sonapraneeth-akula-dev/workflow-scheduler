using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wf.Agent.Limits;
using Wf.Agent.Packages;
using Wf.Agent.Sampling;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Host.V1;

namespace Wf.Agent.Hosts;

internal sealed record HostRecoveryResult(Dispatch Dispatch, string? Error, ResourceSample? ResourceSample = null);

public sealed partial class TaskHostPool
{
    internal TaskHostPackage RecoveredPackage(AttemptRef attempt)
    {
        if (!recovered.TryGetValue(attempt, out var host))
            throw new InvalidOperationException("Recovered task host is no longer available.");
        return new(host.Package.Version, host.Package.Executable, host.Package.Arguments, host.Package.WorkingDirectory);
    }

    internal async Task DiscardRecoveredAsync(AttemptRef attempt)
    {
        if (recovered.TryRemove(attempt, out var host)) await ReleaseAsync(host);
    }

    internal async Task<IReadOnlyList<HostRecoveryResult>> RecoverAsync(
        Func<TaskHostPackage, CancellationToken, Task<TaskHostPackage>> restorePackage,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var leases = recovery.Claim(live.Values.Select(host => host.Containment.LifetimePath).ToHashSet(StringComparer.Ordinal));
            var records = new List<(HostRecoveryBarrier.Lease Lease, TaskHostAttempt Record, Dispatch? Dispatch)>();
            var packages = new Dictionary<HostRecoveryBarrier.Lease, TaskHostPackage>();
            var results = new List<HostRecoveryResult>();
            try
            {
                foreach (var lease in leases)
                {
                    var record = ReadAttempt(lease);
                    var dispatch = ReadDispatch(record);
                    records.Add((lease, record, dispatch));
                }
                // Restore every process-lifetime pin before a normal cache request can evict any package.
                foreach (var (lease, record, _) in records)
                    packages.Add(lease, await restorePackage(new(record.Version, record.Executable,
                        record.Arguments, record.WorkingDirectory), deadline.Token));
                foreach (var (lease, record, dispatch) in records)
                {
                    Process? process = null;
                    TaskHostContainment? containment = null;
                    Host? host = null;
                    CgroupV2Limiter? limiter = null;
                    LinuxTaskResourceScope? scope = null;
                    var physicalStopped = false;
                    var lastSample = record.ResourceSampleBase64 is null ? null :
                        ResourceSample.Parser.ParseFrom(Convert.FromBase64String(record.ResourceSampleBase64));
                    try
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        try
                        {
                            process = Process.GetProcessById(record.Pid);
                            _ = process.Handle;
                            if (process.HasExited || TaskHostLauncher.StartIdentity(process) != record.StartIdentity ||
                                process.HasExited)
                            { process.Dispose(); process = null; }
                        }
                        catch (ArgumentException) { process?.Dispose(); process = null; }
                        catch (FileNotFoundException) { process?.Dispose(); process = null; }
                        catch (DirectoryNotFoundException) { process?.Dispose(); process = null; }
                        containment = new TaskHostContainment(lease, record, process);
                        if (process is null)
                        {
                            await containment.StopAsync(null);
                            physicalStopped = true;
                            RemoveDeadCgroup(record);
                            containment.Complete();
                            File.Delete(record.SocketPath);
                            Directory.Delete(Path.GetDirectoryName(record.SocketPath)!);
                            if (dispatch is not null) results.Add(new(dispatch, "Task host is dead or its PID creation identity changed.", lastSample));
                            continue;
                        }
                        var package = packages[lease];
                        var limits = new TaskResourceLimits(record.Attempt?.CpuCores ?? 0, record.Attempt?.RamMb ?? 0);
                        if (record.WorkloadCgroup is { } cgroup)
                        {
                            if (linuxCgroupRoot is not null && !cgroup.StartsWith(
                                Path.TrimEndingDirectorySeparator(linuxCgroupRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                                throw new InvalidDataException("Recovered workload cgroup is outside the delegated root.");
                            limiter = new(cgroup, limits, reopen: true);
                            var parent = Path.GetDirectoryName(cgroup)!;
                            if (Path.GetFileName(parent).StartsWith("wf-task-", StringComparison.Ordinal))
                                scope = new(parent, process);
                        }
                        var channel = CreateChannel(record.SocketPath, record.ReceiptToken);
                        host = new(package, Path.GetDirectoryName(record.SocketPath)!, record.SocketPath,
                            process, channel, logger, package.CacheLease?.Retain(), scope,
                            scope is not null, "Agent restarted; the original native accounting scope cannot be resumed.",
                            containment, limiter, adopted: true) { ResourceSequence = lastSample?.SampleSequence ?? 0 };
                        if (!live.TryAdd(process.Id, host))
                            throw new InvalidDataException("Multiple recovery records identify the same process.");
                        if (limits.CpuCores > 0 || limits.RamMb > 0)
                        {
                            host.Limiter = OperatingSystem.IsWindows()
                                ? new JobObjectLimiter(process, limits, containment.ResourceJobName, reopen: true)
                                : limiter ?? throw new InvalidDataException("Recovered task has no required workload cgroup.");
                            if (OperatingSystem.IsWindows())
                                logger.LogWarning("Recovered host {Pid} retains verified CPU/RAM hard caps; old Windows IOCP breach notifications cannot be replayed.",
                                    process.Id);
                        }
                        using var readyCall = host.Client.ReadyAsync(new ReadyRequest(), cancellationToken: deadline.Token);
                        var ready = await readyCall.ResponseAsync;
                        if (ready.ProtocolVersion != 8 || ready.ProcessId != process.Id || ready.WorkflowVersion != record.Version ||
                            dispatch is null && ready.Bound || dispatch is not null && (!ready.Bound ||
                                !ready.Attempt.Equals(dispatch.Task) || ready.ReservationEpoch != dispatch.ReservationEpoch))
                            throw new InvalidDataException("Authenticated task-host recovery identity or bound attempt does not match.");
                        containment.VerifyReady();
                        if (dispatch is null) await ReleaseAsync(host);
                        else
                        {
                            if (!recovered.TryAdd(dispatch.Task.Clone(), host))
                                throw new InvalidDataException("More than one surviving host is bound to the same attempt.");
                            results.Add(new(dispatch, null, lastSample));
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(error, "Task-host recovery failed for recorded PID {Pid}; the delegate will not be restarted.", record.Pid);
                        try
                        {
                            if (host is not null) await ReleaseAsync(host);
                            else if (containment is not null)
                            {
                                if (physicalStopped)
                                {
                                    containmentFailure = error;
                                    containment.Dispose();
                                    if (dispatch is not null)
                                        results.Add(new(dispatch, "Dead task-host runtime cleanup failed; machine remains quarantined.", lastSample));
                                    continue;
                                }
                                await containment.StopAsync(process);
                                containment.Complete();
                                limiter?.Dispose();
                                scope?.Dispose();
                                process?.Dispose();
                            }
                            else
                            {
                                lease.Abandon();
                                containmentFailure = error;
                            }
                        }
                        catch (Exception cleanup)
                        {
                            containmentFailure = cleanup;
                            containment?.Dispose();
                            logger.LogError(cleanup, "Unproven physical cleanup keeps recorded PID {Pid} quarantined.", record.Pid);
                        }
                        if (dispatch is not null) results.Add(new(dispatch, "Task-host re-adoption failed; physical cleanup remains subject to reservation fencing.", lastSample));
                    }
                }
                return results;
            }
            catch
            {
                foreach (var host in live.Values.Where(host => leases.Any(lease =>
                    lease.LifetimePath == host.Containment.LifetimePath)))
                    try { await ReleaseAsync(host); }
                    catch (Exception cleanup)
                    {
                        containmentFailure = cleanup;
                        logger.LogError(cleanup, "Aborted host recovery could not prove physical cleanup.");
                    }
                foreach (var lease in leases) lease.Abandon();
                throw;
            }
            finally { foreach (var package in packages.Values) package.Dispose(); }
        }
        finally { gate.Release(); }
    }

    private void RemoveDeadCgroup(TaskHostAttempt record)
    {
        if (record.WorkloadCgroup is not { } cgroup) return;
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(cgroup) ||
            !Path.GetFileName(cgroup).StartsWith("wf-limits-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(cgroup)["wf-limits-".Length..], "N", out _) ||
            linuxCgroupRoot is not null && !cgroup.StartsWith(
                Path.TrimEndingDirectorySeparator(linuxCgroupRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Dead task-host workload cgroup has an invalid ownership path.");
        if (Directory.Exists(cgroup)) Directory.Delete(cgroup);
        var parent = Path.GetDirectoryName(cgroup)!;
        var name = Path.GetFileName(parent);
        if (name.StartsWith("wf-task-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name["wf-task-".Length..], "N", out _) && Directory.Exists(parent))
        {
            Directory.Delete(Path.Combine(parent, "supervisor"));
            Directory.Delete(parent);
        }
    }

    private static TaskHostAttempt ReadAttempt(HostRecoveryBarrier.Lease lease)
    {
        var path = Path.Combine(lease.AttemptDirectory, "attempt.json");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > 6 * 1024 * 1024)
            throw new InvalidDataException("Task-host attempt metadata exceeds its recovery size limit.");
        var record = JsonSerializer.Deserialize(stream, TaskHostAttemptJson.Default.TaskHostAttempt) ??
            throw new InvalidDataException("Task-host attempt metadata is empty.");
        if (record.FormatVersion != 1 || record.BootId != lease.BootId || record.Pid <= 0 ||
            record.StartIdentity == 0 || record.StartedUtcTicks <= 0 || !Guid.TryParseExact(record.ReceiptToken, "N", out _) ||
            string.IsNullOrWhiteSpace(record.Version) || string.IsNullOrWhiteSpace(record.Executable) ||
            record.Arguments is null || record.Arguments.Any(argument => argument is null) ||
            !Path.IsPathFullyQualified(record.WorkingDirectory) || !Path.IsPathFullyQualified(record.SocketPath) ||
            Encoding.UTF8.GetByteCount(record.SocketPath) >= 108 || Path.GetFileName(record.SocketPath) != "s" ||
            OperatingSystem.IsWindows() && record.JobName != $"Local\\wf-task-host-{record.ReceiptToken}" ||
            OperatingSystem.IsLinux() && record.JobName is not null)
            throw new InvalidDataException("Task-host attempt metadata has an invalid format or identity.");
        var socketDirectory = Path.GetDirectoryName(record.SocketPath)!;
        var name = Path.GetFileName(socketDirectory);
        if (!name.StartsWith("wf-h-", StringComparison.Ordinal) || !Guid.TryParseExact(name[5..], "N", out _) ||
            !string.Equals(Path.GetDirectoryName(socketDirectory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Recovered task-host socket is outside its private runtime directory.");
        return record;
    }

    private static Dispatch? ReadDispatch(TaskHostAttempt record)
    {
        if (record.Attempt is null)
        {
            if (record.DispatchBase64 is not null || record.ResourceSampleBase64 is not null)
                throw new InvalidDataException("Unbound host metadata cannot contain a dispatch.");
            return null;
        }
        if (record.DispatchBase64 is null)
            throw new InvalidDataException("Bound host metadata has no recoverable dispatch.");
        var dispatch = Dispatch.Parser.ParseFrom(Convert.FromBase64String(record.DispatchBase64));
        var attempt = record.Attempt;
        if (dispatch.Task is not { } task || !Guid.TryParse(task.RunId, out var run) || run == Guid.Empty ||
            task.RunId != attempt.RunId || task.NodeKey != attempt.NodeKey || task.TaskKey != attempt.TaskKey ||
            task.Attempt != attempt.Attempt || task.Attempt is 0 or > 32767 ||
            string.IsNullOrWhiteSpace(task.NodeKey) || task.NodeKey.Contains('/') ||
            string.IsNullOrWhiteSpace(task.TaskKey) || task.TaskKey.Contains('/') ||
            dispatch.Version != record.Version || dispatch.ReservationEpoch == 0 ||
            dispatch.ReservationEpoch != attempt.ReservationEpoch || dispatch.DeadlineAt is null ||
            dispatch.DeadlineAt.ToDateTimeOffset() != attempt.DeadlineAt ||
            (dispatch.Limits?.CpuCores ?? 0) != attempt.CpuCores || (dispatch.Limits?.RamMb ?? 0) != attempt.RamMb)
            throw new InvalidDataException("Recovered dispatch does not match its durable attempt identity.");
        _ = new TaskResourceLimits(attempt.CpuCores, attempt.RamMb);
        if (record.ResourceSampleBase64 is { } sampleBytes)
        {
            var sample = ResourceSample.Parser.ParseFrom(Convert.FromBase64String(sampleBytes));
            if (!sample.Task.Equals(task) || sample.ReservationEpoch != dispatch.ReservationEpoch ||
                sample.SampleSequence is 0 or > long.MaxValue)
                throw new InvalidDataException("Recovered resource sample does not match its attempt.");
            _ = Wf.Contracts.Resources.ResourceContract.Parse(sample.SampleJson.Memory);
        }
        return dispatch;
    }
}
