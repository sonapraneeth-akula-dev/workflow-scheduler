using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
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
    internal string Token => receiptToken;
    internal string ResourceJobName => $"Local\\wf-task-limits-{receiptToken}";
    internal string LifetimePath => recovery.LifetimePath;

    public TaskHostContainment(HostRecoveryBarrier barrier, ProcessStartInfo start, string socketPath)
    {
        receiptPath = socketPath + ".lifetime";
        recovery = barrier.Begin();
        try
        {
            start.RedirectStandardInput = true;
            start.Environment["WF_TASK_HOST_CONTAINMENT"] = "1";
            start.Environment["WF_TASK_HOST_CONTAINMENT_TOKEN"] = receiptToken;
            start.Environment.Remove("WF_TASK_HOST_SUPERVISOR_PID");
            start.Environment.Remove("WF_TASK_HOST_LIFETIME_JOB");
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
        if (OperatingSystem.IsWindows())
        {
            job = OpenJob(0x1F003F, false, jobName!);
            if (job.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new Win32Exception(error, "Open task-host lifetime job failed; descendant quiescence is unproven.");
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

    public Task StopAsync(Process? process)
    {
        lock (sync) return stopping ??= StopCoreAsync(process);
    }

    private async Task StopCoreAsync(Process? process)
    {
        if (!bound)
            throw new InvalidOperationException("Task-host containment was not bound; physical quiescence is unproven.");
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
        if (job is null && (!File.Exists(certificate) || File.ReadAllText(certificate) != receiptToken))
            throw new InvalidOperationException("Linux task-host supervisor failed; physical quiescence is unproven and machine remains quarantined.");
    }

    public void Complete()
    {
        lock (sync)
        {
            job?.Dispose();
            File.Delete(receiptPath);
            File.Delete(receiptPath[..^".lifetime".Length] + ".control");
            File.Delete(receiptPath[..^".lifetime".Length] + ".exited");
            recovery.Complete();
            completed = true;
        }
    }

    public void Dispose()
    {
        job?.Dispose();
        recovery.Abandon();
    }

    private bool HasReceipt() => File.Exists(receiptPath) && File.ReadAllText(receiptPath) == receiptToken;

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
