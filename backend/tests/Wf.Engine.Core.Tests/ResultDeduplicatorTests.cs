using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Engine.Core.Results;

namespace Wf.Engine.Core.Tests;

public sealed class ResultDeduplicatorTests
{
    [Fact]
    public void Identity_normalizes_run_and_qualifies_task_with_its_node()
    {
        var run = Guid.NewGuid();
        var reference = new AttemptRef { RunId = run.ToString("B").ToUpperInvariant(), NodeKey = "node", TaskKey = "task", Attempt = 2 };
        Assert.Equal(new ResultIdentity(run, "node/task", 2), ResultDeduplicator.Identity(reference));
        reference.NodeKey = "other";
        Assert.NotEqual(new ResultIdentity(run, "node/task", 2), ResultDeduplicator.Identity(reference));
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded)]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.TimedOut)]
    [InlineData(ExecutionStatus.Skipped)]
    public void Terminal_attempts_ignore_replayed_started_and_completed(ExecutionStatus status)
    {
        Assert.True(ResultDeduplicator.IsDuplicate(status, completion: true));
        Assert.True(ResultDeduplicator.IsDuplicate(status, completion: false));
    }

    [Fact]
    public void Running_attempt_accepts_completion_but_not_a_second_started_event()
    {
        Assert.False(ResultDeduplicator.IsDuplicate(ExecutionStatus.Running, completion: true));
        Assert.True(ResultDeduplicator.IsDuplicate(ExecutionStatus.Running, completion: false));
        Assert.False(ResultDeduplicator.IsDuplicate(ExecutionStatus.Dispatched, completion: true));
    }

    [Theory]
    [InlineData("invalid", "node", "task", 1)]
    [InlineData("00000000-0000-0000-0000-000000000000", "node", "task", 1)]
    [InlineData("00000000-0000-0000-0000-000000000001", "n/ode", "task", 1)]
    [InlineData("00000000-0000-0000-0000-000000000001", "node", "task", 0)]
    [InlineData("00000000-0000-0000-0000-000000000001", "node", "task", 32768)]
    public void Invalid_identity_cannot_bypass_deduplication(string run, string node, string task, uint attempt) =>
        Assert.Throws<ArgumentException>(() => ResultDeduplicator.Identity(new AttemptRef
        {
            RunId = run, NodeKey = node, TaskKey = task, Attempt = attempt
        }));

    [Fact]
    public void Duplicate_payload_still_requires_a_valid_result_shape()
    {
        var result = new TaskCompleted
        {
            Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "n", TaskKey = "t", Attempt = 1 },
            Outcome = TaskOutcome.Succeeded, OutputsJson = ByteString.CopyFromUtf8("[]"),
            At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
        Assert.Throws<ArgumentException>(() => ResultDeduplicator.Validate(result));
    }
}
