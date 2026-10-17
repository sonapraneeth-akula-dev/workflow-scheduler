using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts;

public static class CalibrationProtocol
{
    public static CapacityProfile Validate(Calibrate command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentity(command.Id, command.ReservationEpoch, command.DeadlineAt);
        if (Encoding.UTF8.GetByteCount(command.ProfileJson) > 2000)
            throw new InvalidDataException("Calibration profile exceeds 2000 UTF-8 bytes.");
        try
        {
            return JsonSerializer.Deserialize(command.ProfileJson, CapacityProfileJsonContext.Default.CapacityProfile)
                ?? throw new InvalidDataException("Calibration requires a profile.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Invalid calibration profile.", error);
        }
    }

    public static Guid Validate(CalibrationCompleted result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var id = ValidateIdentity(result.Id, result.ReservationEpoch, result.At);
        if (!System.Enum.IsDefined(result.Outcome) || result.Outcome == CalibrationOutcome.Unspecified ||
            (result.Outcome == CalibrationOutcome.Succeeded ? result.CapacityRps <= 0 : result.CapacityRps != 0))
            throw new InvalidDataException("Calibration requires a terminal outcome and a positive rate only on success.");
        return id;
    }

    private static Guid ValidateIdentity(string id, ulong epoch, Timestamp? at)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.Version != 7 || epoch is <= 1 or > long.MaxValue || at is null)
            throw new InvalidDataException("Calibration requires a UUIDv7, machine fence and timestamp.");
        try { _ = at.ToDateTimeOffset(); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid calibration timestamp.", error); }
        return parsed;
    }
}
