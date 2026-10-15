using System.Text.Json;
using Wf.Contracts;
using Wf.Engine.Core.StateMachine;

namespace Wf.Integration.Tests.Execution;

public sealed class TransitionTableTests
{
    [Fact]
    public void SerializedStatusSequenceCanBeValidatedAndLateResultCannotChangeTerminalRun()
    {
        var json = JsonSerializer.Serialize(new[] { RunStatus.Queued, RunStatus.Running, RunStatus.Aborting, RunStatus.Aborted });
        var states = JsonSerializer.Deserialize<RunStatus[]>(json)!;
        var diagnostics = new List<string>();
        for (var i = 1; i < states.Length; i++)
            TransitionTable.Require(states[i - 1], states[i], diagnostics.Add);
        Assert.Throws<InvalidOperationException>(() => TransitionTable.Require(states[^1], RunStatus.Succeeded, diagnostics.Add));
        Assert.Single(diagnostics);
    }
}
