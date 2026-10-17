using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wf.Contracts.Definition;

namespace Wf.Agent.Limits;

internal sealed unsafe partial class JobObjectLimiter : IResourceLimiter
{
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle port;
    private volatile bool breached;
    public string? LinuxCgroupPath => null;

    public JobObjectLimiter(Process process, TaskResourceLimits limits)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Job resource limits require 64-bit Windows.");
        job = CreateJob(0, null);
        if (job.IsInvalid) throw Error("Create resource limit job");
        port = CreateIoCompletionPort(-1, 0, 0, 1);
        try
        {
            if (port.IsInvalid) throw Error("Create resource limit notification port");
            var association = new Association { Key = 1, Port = port.DangerousGetHandle() };
            Set(7, &association, (uint)sizeof(Association));
            if (limits.RamMb > 0)
            {
                var memory = new ExtendedLimits { Basic = new() { Flags = 0x200 },
                    JobMemory = checked((nuint)(limits.RamMb * 1024 * 1024)) };
                Set(9, &memory, (uint)sizeof(ExtendedLimits));
            }
            if (limits.CpuCores > 0)
            {
                if (GetActiveProcessorGroupCount() != 1)
                    throw new PlatformNotSupportedException("CPU core limits require a single Windows processor group.");
                var processors = GetActiveProcessorCount(ushort.MaxValue);
                if (processors == 0) throw Error("Read active processor count");
                var rate = checked((uint)Math.Floor(Math.Min(1, limits.CpuCores / processors) * 10_000));
                if (rate == 0)
                    throw new PlatformNotSupportedException("Requested CPU cap is below Windows Job CPU-rate granularity.");
                var cpu = new CpuRate { Flags = 1 | 4, Rate = rate };
                Set(15, &cpu, (uint)sizeof(CpuRate));
            }
            if (Assign(job, process.Handle) == 0) throw Error("Assign task host to resource limit job");
        }
        catch { port.Dispose(); job.Dispose(); throw; }
    }

    public bool MemoryLimitExceeded
    {
        get
        {
            while (GetQueuedCompletionStatus(port, out var message, out _, out _, 0) != 0)
                if (message == 10) breached = true;
            if (Marshal.GetLastPInvokeError() != 258) throw Error("Read job resource limit notification");
            return breached;
        }
    }

    private void Set(int information, void* value, uint size)
    {
        if (SetInformation(job, information, value, size) == 0) throw Error("Set job resource limit");
    }

    private static Win32Exception Error(string operation) => new(Marshal.GetLastPInvokeError(), operation + " failed.");
    public void Dispose() { port.Dispose(); job.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Association { public nint Key, Port; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRate { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateJob(nint attributes, string? name);
    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    private static partial int Assign(SafeFileHandle job, nint process);
    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    private static partial int SetInformation(SafeFileHandle job, int information, void* value, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateIoCompletionPort(nint file, nint existingPort, nuint key, uint threads);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetQueuedCompletionStatus(SafeFileHandle port, out uint bytes, out nuint key, out nint overlapped, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetActiveProcessorCount(ushort group);
    [LibraryImport("kernel32.dll")]
    private static partial ushort GetActiveProcessorGroupCount();
}
