using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Progress;

namespace Wf.Contracts.Tests;

public sealed class TaskProgressProtocolTests
{
    [Fact]
    public void Every_fixed_phase_roundtrips_with_its_attempt_and_reservation_fence()
    {
        foreach (var phase in System.Enum.GetValues<RemoteOperationPhase>().Where(phase => phase != RemoteOperationPhase.Unspecified))
        {
            var progress = Valid();
            progress.Phase = phase;
            var envelope = AgentMessage.Parser.ParseFrom(new AgentMessage { TaskProgress = progress }.ToByteArray());
            Assert.Equal(AgentMessage.PayloadOneofCase.TaskProgress, envelope.PayloadCase);
            Assert.Equal(progress, envelope.TaskProgress);
            Assert.Equal(new(1, phase, progress.At.ToDateTimeOffset()), TaskProgressProtocol.Validate(envelope.TaskProgress));
        }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("empty-run")]
    [InlineData("run-case")]
    [InlineData("node")]
    [InlineData("task")]
    [InlineData("control")]
    [InlineData("long-key")]
    [InlineData("attempt")]
    [InlineData("attempt-overflow")]
    [InlineData("epoch")]
    [InlineData("epoch-overflow")]
    [InlineData("sequence")]
    [InlineData("sequence-overflow")]
    [InlineData("phase")]
    [InlineData("unknown-phase")]
    [InlineData("time")]
    [InlineData("invalid-time")]
    [InlineData("old-time")]
    [InlineData("sub-microsecond")]
    [InlineData("sub-tick")]
    public void Malformed_progress_never_crosses_the_protocol_boundary(string field)
    {
        var progress = Valid();
        switch (field)
        {
            case "run": progress.Task.RunId = "invalid"; break;
            case "empty-run": progress.Task.RunId = Guid.Empty.ToString("D"); break;
            case "run-case": progress.Task.RunId = progress.Task.RunId.ToUpperInvariant(); break;
            case "node": progress.Task.NodeKey = "node/task"; break;
            case "task": progress.Task.TaskKey = ""; break;
            case "control": progress.Task.TaskKey = "task\n"; break;
            case "long-key": progress.Task.TaskKey = new string('a', 513); break;
            case "attempt": progress.Task.Attempt = 0; break;
            case "attempt-overflow": progress.Task.Attempt = 32768; break;
            case "epoch": progress.ReservationEpoch = 0; break;
            case "epoch-overflow": progress.ReservationEpoch = ulong.MaxValue; break;
            case "sequence": progress.ProgressSequence = 0; break;
            case "sequence-overflow": progress.ProgressSequence = TaskProgressProtocol.MaximumTransitions + 1; break;
            case "phase": progress.Phase = RemoteOperationPhase.Unspecified; break;
            case "unknown-phase": progress.Phase = (RemoteOperationPhase)99; break;
            case "time": progress.At = null; break;
            case "invalid-time": progress.At.Nanos = -1; break;
            case "old-time": progress.At = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddSeconds(-1)); break;
            case "sub-microsecond": progress.At.Nanos = 100; break;
            case "sub-tick": progress.At.Nanos = 1; break;
        }
        Assert.Throws<ArgumentException>(() => TaskProgressProtocol.Validate(progress));
    }

    private static TaskProgress Valid() => new()
    {
        Task = new() { RunId = "0199eaab-0000-7000-8000-000000000001", NodeKey = "work", TaskKey = "task", Attempt = 1 },
        ReservationEpoch = 1, ProgressSequence = 1, Phase = RemoteOperationPhase.Waiting,
        At = Timestamp.FromDateTimeOffset(new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero))
    };
}
