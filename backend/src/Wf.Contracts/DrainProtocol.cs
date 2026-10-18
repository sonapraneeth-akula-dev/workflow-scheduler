using System.Text;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts;

public static class DrainProtocol
{
    public static Guid Validate(Drain command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var id = Validate(command.Id, command.ReservationEpoch);
        if (string.IsNullOrWhiteSpace(command.Reason) || Encoding.UTF8.GetByteCount(command.Reason) > 1024 ||
            command.DeadlineAt is null)
            throw new ArgumentException("Drain requires a bounded reason and deadline.");
        try { _ = command.DeadlineAt.ToDateTimeOffset(); }
        catch (InvalidOperationException error) { throw new ArgumentException("Invalid drain deadline.", error); }
        return id;
    }

    public static Guid Validate(DrainCompleted result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Validate(result.Id, result.ReservationEpoch);
    }

    private static Guid Validate(string text, ulong epoch)
    {
        if (!Guid.TryParseExact(text, "D", out var id) || id.Version != 7 || text != id.ToString("D") ||
            epoch is 0 or > long.MaxValue)
            throw new ArgumentException("Drain requires a canonical UUIDv7 and positive reservation epoch.");
        return id;
    }
}
