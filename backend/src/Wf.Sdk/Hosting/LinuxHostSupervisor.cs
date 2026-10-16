using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wf.Sdk.Hosting;

internal static class LinuxHostSupervisor
{
    public static bool Required => OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("WF_TASK_HOST_CONTAINMENT") == "1";

    public static async Task<int> RunAsync(string socketPath, CancellationToken cancellationToken)
    {
        if (IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Linux task-host pidfd containment requires a 64-bit process.");
        if (Prctl(36, 1, 0, 0, 0) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Task-host child-subreaper containment is unavailable.");
        // Probe before starting workflow code. Older kernels/seccomp policies must fail closed.
        using (var self = OpenPid(Environment.ProcessId))
            if (SyscallSignal(424, self.DangerousGetHandle().ToInt32(), 0, 0, 0) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Task-host containment requires pidfd_send_signal.");
        var token = Environment.GetEnvironmentVariable("WF_TASK_HOST_CONTAINMENT_TOKEN");
        if (!Guid.TryParseExact(token, "N", out _))
            throw new InvalidOperationException("Task-host containment receipt token is unavailable.");
        using (var receipt = new FileStream(socketPath + ".lifetime", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            receipt.Write(System.Text.Encoding.UTF8.GetBytes(token));
            receipt.Flush(flushToDisk: true);
        }
        var commandLine = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes("/proc/self/cmdline"));
        if (commandLine.Length == 0 || commandLine[^1] != '\0')
            throw new InvalidDataException("Task-host command line cannot be safely re-executed.");
        var arguments = commandLine[..^1].Split('\0');
        var start = new ProcessStartInfo(arguments[0]) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
        start.Environment["WF_TASK_HOST_CONTAINMENT"] = "worker";
        start.Environment["WF_TASK_HOST_SUPERVISOR_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Task-host worker did not start.");
        worker.StandardInput.Close();
        var exited = worker.WaitForExitAsync();
        var parentGone = Task.Run(() => Console.OpenStandardInput().ReadByte());
        var stop = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(exited, parentGone, stop);
        var forced = completed != exited;
        if (forced) KillChildren();
        await exited;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            KillChildren();
            while (WaitPid(-1, out _, 1) > 0) { }
            if (Children().Length == 0) break;
            if (watch.Elapsed > TimeSpan.FromSeconds(8))
                throw new TimeoutException("Task-host descendant cleanup could not establish physical quiescence.");
            await Task.Delay(10, CancellationToken.None);
        }
        // Zero is the supervisor's physical-quiescence certificate, not the workflow's exit status.
        return 0;
    }

    private static int[] Children()
    {
        var children = new HashSet<int>();
        foreach (var thread in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try
            {
                foreach (var value in File.ReadAllText(Path.Combine(thread, "children"))
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    children.Add(int.Parse(value, CultureInfo.InvariantCulture));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return children.ToArray();
    }

    private static void KillChildren()
    {
        foreach (var pid in Children())
        {
            using var handle = OpenPid(pid);
            if (handle.IsInvalid) continue;
            // Recheck after opening a stable pidfd: never signal a PID reused by an unrelated process.
            if (!Children().Contains(pid)) continue;
            if (SyscallSignal(424, handle.DangerousGetHandle().ToInt32(), 9, 0, 0) != 0 &&
                Marshal.GetLastPInvokeError() != 3)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Terminate owned task-host descendant failed.");
        }
    }

    private static SafeFileHandle OpenPid(int pid)
    {
        var fd = SyscallOpen(434, pid, 0);
        if (fd < 0 && Marshal.GetLastPInvokeError() != 3)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Task-host containment requires pidfd_open.");
        return new SafeFileHandle((nint)fd, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, nuint value, nuint arg3, nuint arg4, nuint arg5);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallOpen(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallSignal(long number, int fd, int signal, nint info, uint flags);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int pid, out int status, int options);
}
