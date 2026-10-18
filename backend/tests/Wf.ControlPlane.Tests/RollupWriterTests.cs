using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Maintenance;

namespace Wf.ControlPlane.Tests;

public sealed class RollupWriterTests
{
    [Theory]
    [InlineData(RunStatus.Queued, false)]
    [InlineData(RunStatus.Running, false)]
    [InlineData(RunStatus.Aborting, false)]
    [InlineData(RunStatus.Succeeded, true)]
    [InlineData(RunStatus.Failed, true)]
    [InlineData(RunStatus.Aborted, true)]
    [InlineData(RunStatus.TimedOut, true)]
    public void Only_terminal_run_events_refresh_rollups(RunStatus status, bool expected)
    {
        var id = Guid.CreateVersion7();
        var change = new RunEvent(id, 1, DateTimeOffset.UtcNow, EventEntity.Run, null, (int)status);
        Assert.Equal(expected ? [id] : Array.Empty<Guid>(), RollupWriter.TerminalRunIds([change]));
    }

    [Fact]
    public void Mixed_batches_ignore_node_and_task_statuses_and_deduplicate_run_replay()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        RunEvent Event(Guid id, EventEntity entity, int state) => new(id, 1, at, entity,
            entity == EventEntity.Run ? null : "node", state);
        Assert.Equal(new[] { first, second }.Order(), RollupWriter.TerminalRunIds([
            Event(second, EventEntity.Run, (int)RunStatus.Failed),
            Event(first, EventEntity.Node, (int)ExecutionStatus.Running),
            Event(Guid.CreateVersion7(), EventEntity.Task, (int)ExecutionStatus.Retrying),
            Event(first, EventEntity.Run, (int)RunStatus.Succeeded),
            Event(second, EventEntity.Run, (int)RunStatus.Failed)]));
    }
}
