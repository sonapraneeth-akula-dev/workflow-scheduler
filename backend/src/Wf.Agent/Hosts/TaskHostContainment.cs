using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Wf.Agent.Hosts;

internal sealed partial class TaskHostContainment : IDisposable
{
    private readonly SafeFileHandle? job;
    private readonly HostRecoveryBarrier.Lease recovery;
    private readonly string receiptPath;
    private readonly string receiptToken = Guid.NewGuid().ToString("N");
    private readonly string? jobName;
    private readonly object sync = new();
    private Task? stopping;
    private bool bound;
    private long startedUtcTicks;
    private ulong startIdentity;
    private readonly bool adopted;
    private TaskHostAttempt? attempt;
    private bool completed;
    private bool physicalCertificate;
    private Process? guard;
    internal string Token => receiptToken;
    internal string ResourceJobName => $"Local\\wf-task-limits-{receiptToken}";
    internal string LifetimePath => recovery.LifetimePath;
    internal string AttemptDirectory => recovery.AttemptDirectory;
    internal long GuardWorkingSet
    {
        get
        {
            if (guard is null) return 0;
            guard.Refresh();
            if (guard.HasExited) throw new InvalidOperationException("Task-host deadline guard exited before its warm host.");
            return guard.WorkingSet64;
        }
    }

    public TaskHostContainment(HostRecoveryBarrier barrier, ProcessStartInfo start, string socketPath)
    {
        receiptPath = socketPath + ".lifetime";
        recovery = barrier.Begin();
        try
        {
            start.RedirectStandardInput = true;
            start.Environment["WF_TASK_HOST_CONTAINMENT"] = "1";
            start.Environment["WF_TASK_HOST_CONTAINMENT_TOKEN"] = receiptToken;
            start.Environment["WF_TASK_HOST_ATTEMPT_DIRECTORY"] = recovery.AttemptDirectory;
            start.Environment.Remove("WF_TASK_HOST_SUPERVISOR_PID");
            start.Environment.Remove("WF_TASK_HOST_LIFETIME_JOB");
            start.Environment.Remove("WF_TASK_HOST_GUARD");
            start.Environment.Remove("WF_TASK_HOST_GUARD_TARGET");
            start.Environment.Remove("WF_TASK_HOST_GUARD_START");
            if (OperatingSystem.IsWindows())
            {
                jobName = $"Local\\wf-task-host-{receiptToken}";
                start.Environment["WF_TASK_HOST_LIFETIME_JOB"] = jobName;
                job = CreateJob(0, jobName);
                if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Create task-host lifetime job failed.");
            }
            else if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Physical task-host containment requires Windows or Linux.");
        }
        catch { job?.Dispose(); recovery.Complete(); throw; }
    }

    internal TaskHostContainment(HostRecoveryBarrier.Lease lease, TaskHostAttempt record, Process? process)
    {
        recovery = lease;
        receiptPath = record.SocketPath + ".lifetime";
        receiptToken = record.ReceiptToken;
        jobName = record.JobName;
        startedUtcTicks = record.StartedUtcTicks;
        startIdentity = record.StartIdentity;
        adopted = true;
        attempt = record;
        if (HasAgentPhysicalAcknowledgement())
        {
            if (process is not null) throw new InvalidDataException("Physically acknowledged task-host identity is still running.");
            physicalCertificate = bound = true;
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            job = OpenJob(0x1F003F, false, jobName!);
            if (job.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                if (error != 2 || process is not null || !(HasPhysicalCertificate() || HasAgentPhysicalAcknowledgement()))
                    throw new Win32Exception(error, "Open task-host lifetime job failed; descendant quiescence is unproven.");
                physicalCertificate = true;
                job = null;
            }
            if (process is not null && (job is null || IsInJob(process.Handle, job, out var member) == 0 || member == 0))
            { job?.Dispose(); throw new InvalidDataException("Recovered process is not a member of its recorded lifetime job."); }
        }
        else if (!HasReceipt())
            throw new InvalidDataException("Recovered supervisor containment receipt is invalid.");
        bound = true;
    }

    public void Bind(Process process)
    {
        if (job is not null && Assign(job, process.Handle) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Assign task host to mandatory lifetime job failed.");
        if (process.HasExited) throw new InvalidOperationException("Task host exited before its process identity could bind.");
        startedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        startIdentity = TaskHostLauncher.StartIdentity(process);
        if (process.HasExited) throw new InvalidOperationException("Task host exited while its process identity was being established.");
        bound = true;
    }

    public void Record(Process process, TaskHostPackage package, string socketPath, string? workloadCgroup,
        Wf.Contracts.Agent.V1.Dispatch? dispatch = null)
    {
        lock (sync)
            attempt = TaskHostLauncher.Record(recovery, process, package, socketPath, jobName, receiptToken, workloadCgroup,
                startedUtcTicks, startIdentity, dispatch);
    }

    public void RecordResourceSample(Wf.Contracts.Agent.V1.ResourceSample sample)
    {
        lock (sync)
        {
            if (completed) return;
            if (attempt?.Attempt is null)
                throw new InvalidOperationException("Resource samples require bound durable host metadata.");
            attempt = attempt with { ResourceSampleBase64 = Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(sample)) };
            TaskHostLauncher.Save(recovery, attempt);
        }
    }

    public void VerifyReady()
    {
        if (job is null && !HasReceipt())
            throw new InvalidDataException("SDK task host did not establish mandatory Linux subreaper containment.");
    }

    public async Task StartGuardAsync(ProcessStartInfo template, Process process, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo(template.FileName) { UseShellExecute = false, WorkingDirectory = template.WorkingDirectory };
        foreach (var argument in template.ArgumentList) start.ArgumentList.Add(argument);
        foreach (var pair in template.Environment) start.Environment[pair.Key] = pair.Value;
        start.Environment["WF_TASK_HOST_GUARD"] = "1";
        start.Environment["WF_TASK_HOST_GUARD_TARGET"] = process.Id.ToString(CultureInfo.InvariantCulture);
        start.Environment["WF_TASK_HOST_GUARD_START"] = process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        guard = Process.Start(start) ?? throw new InvalidOperationException("Independent task-host deadline guard did not start.");
        var ready = Path.Combine(recovery.AttemptDirectory, "guard-ready");
        while (!File.Exists(ready))
        {
            if (guard.HasExited) throw new InvalidOperationException($"Independent task-host deadline guard exited with code {guard.ExitCode}.");
            await Task.Delay(10, cancellationToken);
        }
        if (ReadReceipt(ready) != receiptToken)
            throw new InvalidDataException("Task-host deadline guard readiness identity is invalid.");
    }

    public Task StopAsync(Process? process)
    {
        lock (sync) return stopping ??= StopCoreAsync(process);
    }

    private async Task StopCoreAsync(Process? process)
    {
        if (!bound)
            throw new InvalidOperationException("Task-host containment was not bound; physical quiescence is unproven.");
        if (physicalCertificate) return;
        if (job is not null)
        {
            if (Terminate(job, 137) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Terminate owned task-host lifetime job failed.");
            var until = Stopwatch.StartNew();
            while (ActiveProcesses() != 0)
            {
                if (until.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("Owned task-host job did not become physically empty; machine remains quarantined.");
                await Task.Delay(10);
            }
            if (File.Exists(Path.Combine(recovery.AttemptDirectory, "guard-ready")))
                while (!HasPhysicalCertificate())
                {
                    if (until.Elapsed > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("Task-host deadline guard did not confirm its final physical cleanup.");
                    await Task.Delay(10);
                }
        }
        else
        {
            if (adopted)
            {
                if (process is not null && !process.HasExited)
                {
                    try
                    {
                        using var control = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await control.ConnectAsync(new UnixDomainSocketEndPoint(receiptPath[..^".lifetime".Length] + ".control"), timeout.Token);
                        using var stream = new NetworkStream(control, ownsSocket: false);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(receiptToken), timeout.Token);
                    }
                    catch (Exception error) when (error is SocketException or IOException)
                    {
                        // The supervisor may close control IPC between HasExited and Connect after normal cleanup.
                        // Only its stable process exit plus the final certificate below can resolve that race.
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
            else
            {
                try
                {
                    if (process is not null && !process.HasExited)
                    {
                        await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 });
                        await process.StandardInput.BaseStream.FlushAsync();
                    }
                }
                catch (IOException) when (process?.HasExited == true) { }
                finally { process?.StandardInput.Close(); }
            }
        }
        if (process is not null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var certificate = receiptPath[..^".lifetime".Length] + ".exited";
        if (OperatingSystem.IsLinux() && (!File.Exists(certificate) || File.ReadAllText(certificate) != receiptToken))
            throw new InvalidOperationException("Linux task-host supervisor failed; physical quiescence is unproven and machine remains quarantined.");
    }

    public void Complete()
    {
        lock (sync)
        {
            job?.Dispose();
            RemoveRuntimeReceipts();
            recovery.Complete();
            completed = true;
            guard?.Dispose();
        }
    }

    public void PreserveSpool()
    {
        lock (sync)
        {
            var path = Path.Combine(recovery.AttemptDirectory, "physical-ack");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                file.Write(Encoding.ASCII.GetBytes(receiptToken));
                file.Flush(flushToDisk: true);
            }
            job?.Dispose();
            guard?.Dispose();
            RemoveRuntimeReceipts();
            recovery.Abandon();
            completed = true;
        }
    }

    private void RemoveRuntimeReceipts()
    {
        if (!Directory.Exists(Path.GetDirectoryName(receiptPath))) return;
        File.Delete(receiptPath);
        File.Delete(receiptPath[..^".lifetime".Length] + ".control");
        File.Delete(receiptPath[..^".lifetime".Length] + ".exited");
    }

    public void Dispose()
    {
        job?.Dispose();
        guard?.Dispose();
        recovery.Abandon();
    }

    private bool HasReceipt() => File.Exists(receiptPath) && File.ReadAllText(receiptPath) == receiptToken;
    private bool HasPhysicalCertificate()
    {
        var path = Path.Combine(recovery.AttemptDirectory, "host-exited");
        return File.Exists(path) && ReadReceipt(path) == receiptToken;
    }
    private bool HasAgentPhysicalAcknowledgement() => HasAgentPhysicalAcknowledgement(recovery.AttemptDirectory, receiptToken);
    internal static bool HasAgentPhysicalAcknowledgement(string directory, string token)
    {
        var path = Path.Combine(directory, "physical-ack");
        Wf.Agent.Logs.LogSpooler.RejectLinks(path);
        return File.Exists(path) && ReadReceipt(path) == token;
    }

    private static string ReadReceipt(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length != 32) throw new InvalidDataException("Task-host containment receipt has an invalid size.");
        using var reader = new StreamReader(file, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    private unsafe uint ActiveProcesses()
    {
        Accounting accounting;
        if (Query(job!, 1, &accounting, (uint)sizeof(Accounting), null) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Query owned task-host lifetime job failed.");
        return accounting.ActiveProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateJob(nint attributes, string? name);
    [LibraryImport("kernel32.dll", EntryPoint = "OpenJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenJob(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [LibraryImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    private static partial int IsInJob(nint process, SafeFileHandle job, out int member);
    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    private static partial int Assign(SafeFileHandle job, nint process);
    [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    private static partial int Terminate(SafeFileHandle job, uint exitCode);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    private static unsafe partial int Query(SafeFileHandle job, int information, void* data, uint size, uint* returned);
}
