using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Wf.Contracts.Agent.V1;

namespace Wf.Sdk.Hosting;

internal static class WindowsHostGuard
{
    internal static async Task<int> RunAsync(string version, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows host guard requires Windows Jobs.");
        var token = Environment.GetEnvironmentVariable("WF_TASK_HOST_CONTAINMENT_TOKEN");
        var directory = Environment.GetEnvironmentVariable("WF_TASK_HOST_ATTEMPT_DIRECTORY");
        if (!Guid.TryParseExact(token, "N", out _) || directory is null || !Path.IsPathFullyQualified(directory) ||
            !int.TryParse(Environment.GetEnvironmentVariable("WF_TASK_HOST_GUARD_TARGET"), out var pid) ||
            !long.TryParse(Environment.GetEnvironmentVariable("WF_TASK_HOST_GUARD_START"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var started))
            throw new InvalidDataException("Windows host guard identity is invalid.");
        using var process = Process.GetProcessById(pid);
        _ = process.Handle;
        if (process.HasExited || process.StartTime.ToFileTimeUtc() != started)
            throw new InvalidDataException("Windows host guard refuses a dead or reused PID.");
        using var job = WindowsHostJobs.OpenLifetime(terminate: true) ??
            throw new InvalidOperationException("Windows host guard has no lifetime Job.");
        if (!IsInJob(process.Handle, job, out var member) || !member)
            throw new InvalidDataException("Windows host guard target is not in its expected lifetime Job.");
        AttemptSpool.Write(Path.Combine(directory, "guard-ready"), Encoding.ASCII.GetBytes(token));
        var exitCode = 0u;
        while (!process.HasExited)
        {
            var dispatch = HostDeadline.ReadDispatch(directory, version);
            if (dispatch is not null && HostDeadline.Expired(dispatch))
            {
                HostDeadline.SaveTimeout(directory, dispatch);
                exitCode = 124;
                break;
            }
            await Task.Delay(100, cancellationToken);
        }
        if (!Terminate(job, exitCode))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Deadline guard could not terminate its owned lifetime Job.");
        var wait = Stopwatch.StartNew();
        while (true)
        {
            if (!Query(job, 1, out var accounting, Marshal.SizeOf<Accounting>(), 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Deadline guard could not verify physical Job quiescence.");
            if (accounting.ActiveProcesses == 0) break;
            if (wait.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Deadline guard cannot prove physical descendant quiescence.");
            await Task.Delay(10, cancellationToken);
        }
        try { AttemptSpool.Write(Path.Combine(directory, "host-exited"), Encoding.ASCII.GetBytes(token)); }
        catch (DirectoryNotFoundException) when (!Directory.Exists(directory))
        { /* The live agent already queried the empty Job and removed its lifetime. */ }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsInJob(nint process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Terminate(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Query(SafeFileHandle job, int kind, out Accounting accounting, int size, nint returned);
}
