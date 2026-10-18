using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Wf.Sdk;

namespace Wf.TaskHost.Fixture;

internal static class DrainFixture
{
    public static async Task ExecuteAsync(TaskContext context)
    {
        context.Outputs["pid"] = Environment.ProcessId;
        context.Outputs["attempt"] = checked((int)context.Attempt.Number);
        if (context.Attempt.Number > 1) return;
        var marker = context.Var<string>("checkpointMarker");
        var mode = context.Var<int>("amount");
        using var process = Process.GetCurrentProcess();
        var startIdentity = OperatingSystem.IsWindows()
            ? checked((ulong)process.StartTime.ToFileTimeUtc()) : LinuxStartIdentity();
        using var child = mode == 2 ? Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /c ping -n 120 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 120\"") { UseShellExecute = false }) : null;
        using var cancelled = context.CancellationToken.Register(() => File.WriteAllText(marker + ".cancelled", "1"));
        await File.WriteAllTextAsync(marker + ".next",
            JsonSerializer.Serialize(new { pid = Environment.ProcessId, childPid = child?.Id, startIdentity }), context.CancellationToken);
        File.Move(marker + ".next", marker, overwrite: true);
        if (mode == 2) Thread.Sleep(Timeout.Infinite);
        if (mode == 1)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                while (!File.Exists(marker + ".exit")) await Task.Delay(10);
                context.CancellationToken.ThrowIfCancellationRequested();
            }
        }
        else
            while (!File.Exists(marker + ".exit")) await Task.Delay(10, context.CancellationToken);
    }

    private static ulong LinuxStartIdentity()
    {
        var stat = File.ReadAllText($"/proc/{Environment.ProcessId}/stat");
        return ulong.Parse(stat[(stat.LastIndexOf(')') + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[19], CultureInfo.InvariantCulture);
    }
}
