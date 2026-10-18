using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Hosts;

internal static class TaskHostLauncher
{
    public static TaskHostAttempt Record(HostRecoveryBarrier.Lease lease, Process process, TaskHostPackage package,
        string socketPath, string? jobName, string receiptToken, string? workloadCgroup, long startedUtcTicks,
        ulong startIdentity, Dispatch? dispatch = null)
    {
        var record = new TaskHostAttempt(1, lease.BootId, process.Id, startedUtcTicks, startIdentity,
            socketPath, jobName, receiptToken, package.Version, package.Executable, package.Arguments.ToArray(),
            package.WorkingDirectory, workloadCgroup, dispatch is null ? null :
                new(dispatch.Task.RunId, dispatch.Task.NodeKey, dispatch.Task.TaskKey, dispatch.Task.Attempt,
                    dispatch.ReservationEpoch, dispatch.DeadlineAt.ToDateTimeOffset(),
                    dispatch.Limits?.CpuCores ?? 0, dispatch.Limits?.RamMb ?? 0), 0,
            dispatch is null ? null : Convert.ToBase64String(dispatch.ToByteArray()), null);
        Save(lease, record);
        return record;
    }

    public static void Save(HostRecoveryBarrier.Lease lease, TaskHostAttempt record)
    {
        TaskHostPool.CreatePrivateDirectory(lease.AttemptDirectory);
        var path = Path.Combine(lease.AttemptDirectory, "attempt.json");
        var next = path + ".next";
        using (var file = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(next, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(file, record, TaskHostAttemptJson.Default.TaskHostAttempt);
            file.Flush(flushToDisk: true);
        }
        File.Move(next, path, overwrite: true);
    }

    internal static ulong StartIdentity(Process process)
    {
        if (!OperatingSystem.IsLinux()) return checked((ulong)process.StartTime.ToFileTimeUtc());
        var stat = File.ReadAllText($"/proc/{process.Id}/stat");
        var close = stat.LastIndexOf(')');
        if (close < 0) throw new InvalidDataException("Task-host process stat has no command boundary.");
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started))
            throw new InvalidDataException("Task-host process stat has no valid boot-relative start time.");
        return started;
    }
}

internal sealed record TaskHostAttempt(int FormatVersion, string BootId, int Pid, long StartedUtcTicks, ulong StartIdentity,
    string SocketPath, string? JobName, string ReceiptToken, string Version, string Executable,
    string[] Arguments, string WorkingDirectory, string? WorkloadCgroup, TaskHostAttemptIdentity? Attempt,
    ulong LastAcknowledgedSequence, string? DispatchBase64, string? ResourceSampleBase64);

internal sealed record TaskHostAttemptIdentity(string RunId, string NodeKey, string TaskKey, uint Attempt,
    ulong ReservationEpoch, DateTimeOffset DeadlineAt, double CpuCores, ulong RamMb);

[JsonSerializable(typeof(TaskHostAttempt))]
internal sealed partial class TaskHostAttemptJson : JsonSerializerContext;
