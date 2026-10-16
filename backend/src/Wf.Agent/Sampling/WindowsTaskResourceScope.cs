using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wf.Agent.Sampling;

internal sealed unsafe partial class WindowsTaskResourceScope : ITaskResourceScope
{
    private readonly SafeFileHandle job;
    private readonly Func<WindowsNetworkTrace> trace;
    private WindowsNetworkTrace.Subscription? network;
    private string? networkUnavailable;

    public WindowsTaskResourceScope(Func<WindowsNetworkTrace> trace)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Native job accounting requires a 64-bit process.");
        this.trace = trace;
        job = CreateJob(0, $"Local\\wf-resource-{Guid.NewGuid():N}");
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Create owned resource job failed.");
        if (Marshal.GetLastPInvokeError() == 183)
        {
            job.Dispose();
            throw new InvalidOperationException("Refusing to reuse a pre-existing resource job.");
        }
    }

    public void PrepareStart(ProcessStartInfo startInfo) { }

    public void Bind(Process process)
    {
        if (Assign(job, process.Handle) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Assign task host to its owned resource job failed.");
        try { network = trace().Subscribe(process); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            networkUnavailable = exception.Message;
        }
    }

    public void BeginAttempt() => network?.Begin();
    public void EndAttempt() => network?.End();

    public ResourceCounters ReadCounters()
    {
        Accounting accounting;
        if (Query(job, 1, &accounting, (uint)sizeof(Accounting), null) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Query task-job CPU accounting failed.");
        ulong resident = 0;
        foreach (var pid in Members())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) resident = checked(resident + (ulong)process.WorkingSet64);
            }
            catch (ArgumentException) { }
        }
        var unavailable = networkUnavailable;
        var bytes = network?.Read(out unavailable);
        return new ResourceCounters((ulong)(accounting.User + accounting.Kernel), (ulong)Stopwatch.GetElapsedTime(0).Ticks,
            resident, bytes is null ? null : new Dictionary<string, NetworkBytes> { ["etw-process"] = bytes },
            unavailable, CpuScope: "job-with-descendants", MemoryScope: "job-resident-sum",
            NetworkScope: "host-process-etw-transport");
    }

    private int[] Members()
    {
        var capacity = 32;
        while (true)
        {
            var data = new nuint[capacity + 1];
            fixed (nuint* pointer = data)
            {
                if (Query(job, 3, pointer, (uint)(data.Length * sizeof(nuint)), null) != 0)
                    return new ReadOnlySpan<nuint>(pointer + 1, (int)((uint*)pointer)[1]).ToArray().Select(pid => checked((int)pid)).ToArray();
                var error = Marshal.GetLastPInvokeError();
                if (error != 234) throw new Win32Exception(error, "Query task-job membership failed.");
                capacity = checked(Math.Max(capacity * 2, (int)((uint*)pointer)[0]));
            }
        }
    }

    public void Dispose()
    {
        network?.Dispose();
        job.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateJob(nint attributes, string name);
    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    private static partial int Assign(SafeFileHandle job, nint process);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    private static partial int Query(SafeFileHandle job, int information, void* data, uint size, uint* returned);
}
