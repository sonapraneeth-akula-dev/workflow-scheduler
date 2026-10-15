using Wf.Contracts;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Tests;

public sealed class TransitionTableTests
{
    [Theory]
    [InlineData(RunStatus.Queued, RunStatus.Running)]
    [InlineData(RunStatus.Queued, RunStatus.Aborted)]
    [InlineData(RunStatus.Running, RunStatus.Aborting)]
    [InlineData(RunStatus.Running, RunStatus.Succeeded)]
    [InlineData(RunStatus.Running, RunStatus.Failed)]
    [InlineData(RunStatus.Running, RunStatus.TimedOut)]
    [InlineData(RunStatus.Aborting, RunStatus.Aborted)]
    public void AllDeclaredRunTransitionsAreAcceptedWithoutLogging(RunStatus from, RunStatus to)
    {
        var logs = new List<string>();
        TransitionTable.Require(from, to, logs.Add);
        Assert.Empty(logs);
    }

    [Fact]
    public void ExhaustiveRunMatrixMatchesDesignAndTerminalsAreAbsorbing()
    {
        foreach (var from in Enum.GetValues<RunStatus>())
        {
            var expected = from switch
            {
                RunStatus.Queued => new[] { RunStatus.Running, RunStatus.Aborted },
                RunStatus.Running => [RunStatus.Succeeded, RunStatus.Failed, RunStatus.Aborting, RunStatus.TimedOut],
                RunStatus.Aborting => [RunStatus.Aborted],
                _ => []
            };
            Assert.Equal(expected.Length == 0, TransitionTable.IsTerminal(from));
            foreach (var to in Enum.GetValues<RunStatus>())
                Assert.Equal(expected.Contains(to), TransitionTable.CanTransition(from, to));
        }
    }

    [Fact]
    public void ExhaustiveNodeAndTaskMatrixMatchesDesign()
    {
        foreach (var from in Enum.GetValues<ExecutionStatus>())
        {
            var expected = from switch
            {
                ExecutionStatus.Pending => new[] { ExecutionStatus.Ready, ExecutionStatus.Skipped, ExecutionStatus.Cancelled, ExecutionStatus.Reused },
                ExecutionStatus.Ready => [ExecutionStatus.Dispatched, ExecutionStatus.Failed, ExecutionStatus.Skipped, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
                ExecutionStatus.Dispatched => [ExecutionStatus.Running, ExecutionStatus.Retrying, ExecutionStatus.Failed, ExecutionStatus.TimedOut, ExecutionStatus.Cancelled],
                ExecutionStatus.Running => [ExecutionStatus.Succeeded, ExecutionStatus.SoftFailed, ExecutionStatus.Failed, ExecutionStatus.TimedOut, ExecutionStatus.Cancelled, ExecutionStatus.Retrying],
                ExecutionStatus.Retrying => [ExecutionStatus.Dispatched, ExecutionStatus.Failed, ExecutionStatus.TimedOut, ExecutionStatus.Cancelled, ExecutionStatus.Skipped],
                _ => []
            };
            Assert.Equal(expected.Length == 0, TransitionTable.IsTerminal(from));
            foreach (var to in Enum.GetValues<ExecutionStatus>())
            {
                var logs = new List<string>();
                if (expected.Contains(to))
                {
                    TransitionTable.Require(from, to, logs.Add);
                    Assert.Empty(logs);
                }
                else
                {
                    var error = Assert.Throws<InvalidOperationException>(() => TransitionTable.Require(from, to, logs.Add));
                    Assert.Equal(error.Message, Assert.Single(logs));
                }
            }
        }
    }

    [Fact]
    public void InvalidRunTransitionsAreLoggedAndRejectedWithoutMutatingState()
    {
        var logs = new List<string>();
        var state = RunStatus.Succeeded;
        var error = Assert.Throws<InvalidOperationException>(() => TransitionTable.Require(state, RunStatus.Failed, logs.Add));
        Assert.Equal(RunStatus.Succeeded, state);
        Assert.Equal(error.Message, Assert.Single(logs));
        Assert.False(TransitionTable.CanTransition((RunStatus)99, RunStatus.Queued));
        Assert.False(TransitionTable.IsTerminal((RunStatus)99));
        Assert.False(TransitionTable.IsTerminal((ExecutionStatus)99));
        Assert.Throws<ArgumentNullException>(() => TransitionTable.Require(RunStatus.Queued, RunStatus.Running, null!));
    }
}
