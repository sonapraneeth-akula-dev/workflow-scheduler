using System.Text;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Progress;

public sealed record RemoteOperationProgress(uint Sequence, RemoteOperationPhase Phase, DateTimeOffset OccurredAt);

public static class TaskProgressProtocol
{
    public const uint MaximumTransitions = 64;

    public static RemoteOperationProgress Validate(TaskProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var task = progress.Task;
        if (task is null || !Guid.TryParseExact(task.RunId, "D", out var run) || run == Guid.Empty ||
            task.RunId != run.ToString("D") || InvalidKey(task.NodeKey) || InvalidKey(task.TaskKey) ||
            task.Attempt is 0 or > 32767 || progress.ReservationEpoch is 0 or > long.MaxValue ||
            progress.At is null || progress.At.Nanos % 1000 != 0)
            throw new ArgumentException("Invalid task progress identity.");
        DateTimeOffset at;
        try { at = progress.At.ToDateTimeOffset(); }
        catch (InvalidOperationException) { throw new ArgumentException("Invalid task progress time."); }
        var value = new RemoteOperationProgress(progress.ProgressSequence, progress.Phase, at);
        Validate(value);
        return value;
    }

    public static void Validate(RemoteOperationProgress progress)
    {
        if (progress.Sequence is 0 or > MaximumTransitions || progress.Phase == RemoteOperationPhase.Unspecified ||
            !Enum.IsDefined(progress.Phase) || progress.OccurredAt.Offset != TimeSpan.Zero ||
            progress.OccurredAt < DateTimeOffset.UnixEpoch || progress.OccurredAt.UtcTicks % 10 != 0)
            throw new ArgumentException("Invalid remote operation progress.");
    }

    private static bool InvalidKey(string value) => string.IsNullOrWhiteSpace(value) || value.Contains('/') ||
        value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 512;
}
