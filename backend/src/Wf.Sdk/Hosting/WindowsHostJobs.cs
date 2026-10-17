using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wf.Sdk.Hosting;

internal static class WindowsHostJobs
{
    public static SafeFileHandle? OpenLifetime()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var name = Environment.GetEnvironmentVariable("WF_TASK_HOST_LIFETIME_JOB");
        return name is null ? null : OpenOwned(name, "host");
    }

    public static SafeFileHandle OpenLimits(string name) => OpenOwned(name, "limits");

    private static SafeFileHandle OpenOwned(string name, string kind)
    {
        var token = Environment.GetEnvironmentVariable("WF_TASK_HOST_CONTAINMENT_TOKEN");
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(token, "N", out _) ||
            name != $"Local\\wf-task-{kind}-{token}")
            throw new InvalidDataException("Task-host Windows Job identity is invalid.");
        var handle = OpenJob(4, false, name);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Open owned SDK task-host Job failed.");
        }
        return handle;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJob(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
}
