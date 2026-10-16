using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wf.Agent.Hosts;

internal sealed partial class TaskHostContainment : IDisposable
{
    private readonly SafeFileHandle? job;
    private readonly HostRecoveryBarrier.Lease recovery;
    private readonly string receiptPath;
    private readonly string receiptToken = Guid.NewGuid().ToString("N");
    private readonly object sync = new();
    private Task? stopping;
    private bool bound;

    public unsafe TaskHostContainment(HostRecoveryBarrier barrier, ProcessStartInfo start, string socketPath)
    {
        receiptPath = socketPath + ".lifetime";
        recovery = barrier.Begin();
        try
        {
            start.RedirectStandardInput = true;
            start.Environment["WF_TASK_HOST_CONTAINMENT"] = "1";
            start.Environment["WF_TASK_HOST_CONTAINMENT_TOKEN"] = receiptToken;
            start.Environment.Remove("WF_TASK_HOST_SUPERVISOR_PID");
            if (OperatingSystem.IsWindows())
            {
                job = CreateJob(0, null);
                if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Create task-host lifetime job failed.");
                var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
                if (SetInformation(job, 9, &limits, (uint)sizeof(ExtendedLimits)) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Set task-host KILL_ON_JOB_CLOSE failed.");
            }
            else if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Physical task-host containment requires Windows or Linux.");
        }
        catch { job?.Dispose(); recovery.Complete(); throw; }
    }

    public void Bind(Process process)
    {
        if (job is not null && Assign(job, process.Handle) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Assign task host to mandatory lifetime job failed.");
        bound = true;
    }

    public void VerifyReady()
    {
        if (job is null && !HasReceipt())
            throw new InvalidDataException("SDK task host did not establish mandatory Linux subreaper containment.");
    }

    public Task StopAsync(Process process)
    {
        lock (sync) return stopping ??= StopCoreAsync(process);
    }

    private async Task StopCoreAsync(Process process)
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
            // EOF also occurs on agent SIGKILL. The SDK subreaper remains alive until every descendant is reaped.
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (job is null && (process.ExitCode != 0 || !HasReceipt()))
            throw new InvalidOperationException("Linux task-host supervisor failed; physical quiescence is unproven and machine remains quarantined.");
    }

    public void Complete()
    {
        job?.Dispose();
        File.Delete(receiptPath);
        recovery.Complete();
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
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateJob(nint attributes, string? name);
    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    private static unsafe partial int SetInformation(SafeFileHandle job, int information, void* data, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    private static partial int Assign(SafeFileHandle job, nint process);
    [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    private static partial int Terminate(SafeFileHandle job, uint exitCode);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    private static unsafe partial int Query(SafeFileHandle job, int information, void* data, uint size, uint* returned);
}
