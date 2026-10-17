using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Tests;

public sealed class DrainProtocolTests
{
    [Fact]
    public void Drain_wire_is_additive_and_keeps_physical_release_distinct_from_transport_ack()
    {
        var id = Guid.CreateVersion7();
        var command = new ControlMessage { Drain = new() { Id = id.ToString("D"), ReservationEpoch = 42,
            Reason = "maintenance", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } };
        Assert.Equal(12, ControlMessage.Descriptor.FindFieldByName("drain").FieldNumber);
        Assert.Equal(1, Drain.Descriptor.FindFieldByName("deadline_at").FieldNumber);
        Assert.Equal(2, Drain.Descriptor.FindFieldByName("reason").FieldNumber);
        Assert.Equal(id, DrainProtocol.Validate(ControlMessage.Parser.ParseFrom(command.ToByteArray()).Drain));
        var result = new AgentMessage { DrainCompleted = new() { Id = id.ToString("D"), ReservationEpoch = 42 } };
        Assert.Equal(id, DrainProtocol.Validate(AgentMessage.Parser.ParseFrom(result.ToByteArray()).DrainCompleted));
        Assert.Null(result.Ack);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("epoch")]
    [InlineData("deadline")]
    [InlineData("reason")]
    public void Malformed_drain_cannot_be_accepted_or_journaled(string field)
    {
        var command = new Drain { Id = Guid.CreateVersion7().ToString("D"), ReservationEpoch = 1,
            Reason = "maintenance", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        if (field == "identity") command.Id = Guid.NewGuid().ToString("D");
        if (field == "epoch") command.ReservationEpoch = ulong.MaxValue;
        if (field == "deadline") command.DeadlineAt = null;
        if (field == "reason") command.Reason = new string('x', 1025);
        Assert.Throws<ArgumentException>(() => DrainProtocol.Validate(command));
    }
}
