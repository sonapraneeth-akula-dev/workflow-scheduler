using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Wf.Sdk.Hosting;

internal static class LinuxWorkloadCgroup
{
    public static void Join(string path)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("WF_TASK_HOST_CONTAINMENT") != "worker")
            throw new InvalidOperationException("Only the contained Linux worker may join a workload limit cgroup.");
        if (Environment.GetEnvironmentVariable("WF_TASK_HOST_WORKLOAD_CGROUP") != path)
            throw new InvalidOperationException("Workload cgroup must be established before worker startup.");
        ValidatePath(path);
        File.WriteAllText(Path.Combine(path, "cgroup.procs"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    public static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Workload cgroup path must be absolute.", nameof(path));
        var information = new byte[256];
        if (Statfs(path, information) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Inspect workload cgroup filesystem failed.");
        if (BitConverter.ToInt64(information) != 0x63677270)
            throw new InvalidOperationException("Workload limits require a real cgroup-v2 filesystem.");
    }

    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int Statfs(string path, [Out] byte[] information);
}
