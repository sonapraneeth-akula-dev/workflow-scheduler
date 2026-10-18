using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;

namespace Wf.Sdk.Hosting;

internal static class HostDeadline
{
    internal static Dispatch? ReadDispatch(string directory, string? version = null)
    {
        var path = Path.Combine(directory, "spool", "dispatch.pb");
        if (!File.Exists(path)) return null;
        var dispatch = Dispatch.Parser.ParseFrom(AttemptSpool.Read(path, 4 * 1024 * 1024));
        if (dispatch.Task is null || dispatch.DeadlineAt is null || version is not null && dispatch.Version != version)
            throw new InvalidDataException("Deadline guard dispatch identity is invalid.");
        return dispatch;
    }

    internal static bool Expired(Dispatch dispatch) =>
        DateTimeOffset.UtcNow >= dispatch.DeadlineAt.ToDateTimeOffset().AddSeconds(30);

    internal static void SaveTimeout(string directory, Dispatch dispatch) =>
        AttemptSpool.SaveResult(Path.Combine(directory, "spool"), new TaskCompleted
        {
            Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch, Outcome = TaskOutcome.TimedOut,
            At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Error = new TaskError { Category = "Timeout",
                Message = "Task host exceeded its hard deadline and cancellation grace; its owned process tree was terminated." }
        });
}
