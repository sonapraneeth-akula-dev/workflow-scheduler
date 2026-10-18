using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Tests;

public sealed class CalibrationProtocolTests
{
    [Fact]
    public void Command_requires_strict_profile_and_preserves_canonical_identity()
    {
        var command = Command();
        Assert.Equal(new CapacityProfile("http", "json", "small").ProfileKey, CalibrationProtocol.Validate(command).ProfileKey);
        Assert.Equal(command.Id, CalibrationProtocol.Validate(Result()).ToString("D"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("epoch")]
    [InlineData("epoch-overflow")]
    [InlineData("time")]
    [InlineData("invalid-time")]
    [InlineData("json")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("size")]
    public void Malformed_commands_are_rejected(string field)
    {
        var command = Command();
        switch (field)
        {
            case "id": command.Id = "invalid"; break;
            case "version": command.Id = Guid.NewGuid().ToString("D"); break;
            case "epoch": command.ReservationEpoch = 1; break;
            case "epoch-overflow": command.ReservationEpoch = ulong.MaxValue; break;
            case "time": command.DeadlineAt = null; break;
            case "invalid-time": command.DeadlineAt.Nanos = -1; break;
            case "json": command.ProfileJson = "{"; break;
            case "unknown": command.ProfileJson = """{"transport":"http","codec":"json","payloadSizeClass":"small","extra":1}"""; break;
            case "duplicate": command.ProfileJson = """{"transport":"http","transport":"grpc","codec":"json","payloadSizeClass":"small"}"""; break;
            case "null": command.ProfileJson = "null"; break;
            case "size": command.ProfileJson = new string(' ', 2001); break;
        }
        Assert.Throws<InvalidDataException>(() => CalibrationProtocol.Validate(command));
    }

    [Theory]
    [InlineData(CalibrationOutcome.Succeeded, 1, true)]
    [InlineData(CalibrationOutcome.Succeeded, int.MaxValue, true)]
    [InlineData(CalibrationOutcome.Succeeded, 0, false)]
    [InlineData(CalibrationOutcome.Succeeded, -1, false)]
    [InlineData(CalibrationOutcome.Unsupported, 0, true)]
    [InlineData(CalibrationOutcome.Busy, 0, true)]
    [InlineData(CalibrationOutcome.TimedOut, 0, true)]
    [InlineData(CalibrationOutcome.Failed, 0, true)]
    [InlineData(CalibrationOutcome.Unsupported, 10, false)]
    [InlineData(CalibrationOutcome.Unspecified, 0, false)]
    [InlineData((CalibrationOutcome)99, 0, false)]
    public void Only_success_may_report_a_positive_Int32_rate(CalibrationOutcome outcome, int rate, bool valid)
    {
        var result = Result();
        result.Outcome = outcome;
        result.CapacityRps = rate;
        if (valid) Assert.Equal(Guid.Parse(result.Id), CalibrationProtocol.Validate(result));
        else Assert.Throws<InvalidDataException>(() => CalibrationProtocol.Validate(result));
    }

    private static readonly Guid Id = Guid.CreateVersion7();
    private static Calibrate Command() => new()
    {
        Id = Id.ToString("D"), ProfileJson = new CapacityProfile("http", "json", "small").ProfileKey,
        ReservationEpoch = 2, DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1))
    };
    private static CalibrationCompleted Result() => new()
    {
        Id = Id.ToString("D"), ReservationEpoch = 2, At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Outcome = CalibrationOutcome.Succeeded, CapacityRps = 100
    };
}
