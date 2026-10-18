using System.Text.Json.Serialization;

namespace Wf.Contracts;

public enum AbortSource { UI, API, CLI, KillSwitch, Local }

public sealed record AbortRecord
{
    public string By { get; }
    public string ByName { get; }
    public DateTimeOffset At { get; }
    public string Reason { get; }
    public AbortSource Source { get; }

    public AbortRecord(string by, string byName, DateTimeOffset at, string reason, AbortSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        ArgumentException.ThrowIfNullOrWhiteSpace(byName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 500)
            throw new ArgumentOutOfRangeException(nameof(reason), "Abort reason must contain 1-500 characters.");
        if (at.Offset != TimeSpan.Zero)
            throw new ArgumentException("Abort timestamps must use UTC.", nameof(at));
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        By = by;
        ByName = byName;
        At = at;
        Reason = reason;
        Source = source;
    }
}

[JsonSerializable(typeof(AbortRecord))]
public sealed partial class AbortRecordJsonContext : JsonSerializerContext;
