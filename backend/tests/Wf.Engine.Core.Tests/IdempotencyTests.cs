using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Engine.Core.Results;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Tests;

public sealed class IdempotencyTests
{
    private static readonly Guid RunId = new("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_run_node_task_and_attempt_combination_has_a_distinct_identity()
    {
        var identities = new HashSet<ResultIdentity>();
        foreach (var run in new[] { RunId, new Guid("00000000-0000-0000-0000-000000000002") })
        foreach (var node in new[] { "node", "Node" })
        foreach (var task in new[] { "task", "Task" })
        foreach (var attempt in new short[] { 1, 2, short.MaxValue })
        {
            var reference = new AttemptRef
            {
                RunId = run.ToString("B").ToUpperInvariant(), NodeKey = node, TaskKey = task,
                Attempt = (uint)attempt
            };
            var identity = ResultDeduplicator.Identity(reference);
            Assert.Equal(new ResultIdentity(run, $"{node}/{task}", attempt), identity);
            Assert.True(identities.Add(identity));
            reference.RunId = run.ToString("D");
            Assert.Equal(identity, ResultDeduplicator.Identity(reference));
        }
        Assert.Equal(24, identities.Count);
    }

    [Theory]
    [InlineData(ExecutionStatus.Pending, false, false)]
    [InlineData(ExecutionStatus.Ready, false, false)]
    [InlineData(ExecutionStatus.Dispatched, false, false)]
    [InlineData(ExecutionStatus.Running, true, false)]
    [InlineData(ExecutionStatus.Retrying, false, false)]
    [InlineData(ExecutionStatus.Succeeded, true, true)]
    [InlineData(ExecutionStatus.SoftFailed, true, true)]
    [InlineData(ExecutionStatus.Failed, true, true)]
    [InlineData(ExecutionStatus.TimedOut, true, true)]
    [InlineData(ExecutionStatus.Cancelled, true, true)]
    [InlineData(ExecutionStatus.Skipped, true, true)]
    [InlineData(ExecutionStatus.Reused, true, true)]
    public void Persisted_status_distinguishes_started_replay_from_new_completion(
        ExecutionStatus status, bool startedReplay, bool completedReplay)
    {
        Assert.Equal(startedReplay, ResultDeduplicator.IsDuplicate(status, completion: false));
        Assert.Equal(completedReplay, ResultDeduplicator.IsDuplicate(status, completion: true));
    }

    [Theory]
    [InlineData(ExecutionStatus.Pending)]
    [InlineData(ExecutionStatus.Ready)]
    [InlineData(ExecutionStatus.Dispatched)]
    [InlineData(ExecutionStatus.Retrying)]
    public void A_nonduplicate_success_still_requires_a_valid_transition(ExecutionStatus status)
    {
        Assert.False(ResultDeduplicator.IsDuplicate(status, completion: true));
        Assert.False(TransitionTable.CanTransition(status, ExecutionStatus.Succeeded));
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("timestamp")]
    [InlineData("invalid-timestamp")]
    [InlineData("outputs")]
    [InlineData("json")]
    [InlineData("attempt")]
    public void Even_a_terminal_replay_requires_a_valid_completion_shape(string invalid)
    {
        var completed = Completion();
        Assert.True(ResultDeduplicator.IsDuplicate(ExecutionStatus.Succeeded, completion: true));
        switch (invalid)
        {
            case "outcome": completed.Outcome = (TaskOutcome)99; break;
            case "timestamp": completed.At = null; break;
            case "invalid-timestamp": completed.At = new Timestamp { Nanos = -1 }; break;
            case "outputs": completed.OutputsJson = ByteString.CopyFromUtf8("[]"); break;
            case "json": completed.OutputsJson = ByteString.CopyFromUtf8("{"); break;
            case "attempt": completed.Task.Attempt = 0; break;
        }
        Assert.Throws<ArgumentException>(() => ResultDeduplicator.Validate(completed));
    }

    [Fact]
    public void Valid_completion_replays_keep_the_same_identity_and_payload()
    {
        var completed = Completion();
        foreach (var replay in new[] { completed.Clone(), TaskCompleted.Parser.ParseFrom(completed.ToByteArray()) })
        {
            ResultDeduplicator.Validate(replay);
            Assert.Equal(ResultDeduplicator.Identity(completed.Task), ResultDeduplicator.Identity(replay.Task));
            Assert.Equal(completed, replay);
        }
    }

    private static TaskCompleted Completion() => new()
    {
        Task = new AttemptRef { RunId = RunId.ToString("D"), NodeKey = "node", TaskKey = "task", Attempt = 1 },
        At = Timestamp.FromDateTimeOffset(Now), Outcome = TaskOutcome.Succeeded,
        OutputsJson = ByteString.CopyFromUtf8("""{"value":7}""")
    };
}
