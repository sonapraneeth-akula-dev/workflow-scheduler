using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core.Metrics;

namespace Wf.Engine.Core.Tests;

public sealed class RunMetricsTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    [Fact]
    public void Legacy_events_without_optional_metrics_metadata_keep_their_serialized_payload()
    {
        var change = new RunEvent(Guid.CreateVersion7(), 1, Epoch, EventEntity.Run, null, (int)RunStatus.Running);
        var legacy = new { change.RunId, change.Sequence, change.At, change.Entity, change.Key, change.State, change.Abort };
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(change));
    }

    [Fact]
    public void Proposals_and_projection_are_pure_and_replay_uses_only_event_timestamps()
    {
        var run = NewRun();
        var initial = run.Snapshot;
        var change = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, At(7));
        var projected = RunMetricsRecorder.Record(initial.Metrics!, change);
        Assert.Same(initial, run.Snapshot);
        Assert.Empty(run.Events);
        Assert.Equal(7000, projected.Run.QueueWaitMs);
        run.Apply(change);
        Assert.Equal(projected, run.Snapshot.Metrics);
        Task(run, ExecutionStatus.Ready, 8);
        Task(run, ExecutionStatus.Dispatched, 10);
        Task(run, ExecutionStatus.Running, 13);
        Task(run, ExecutionStatus.Succeeded, 17);
        run.Append(EventEntity.Run, null, (int)RunStatus.Succeeded, At(20));
        var replay = RunState.Restore(initial, run.Events, Assert.Fail);
        AssertMetrics(run.Snapshot.Metrics!, replay.Snapshot.Metrics!);
        var metric = run.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Equal(5000, metric.QueueWaitMs);
        Assert.Equal(4000, metric.DurationMs);
        Assert.Equal((int)ExecutionStatus.Succeeded, metric.TerminalStatus);
        Assert.Equal(At(13), metric.StartedAt);
        Assert.Equal(At(17), metric.EndedAt);
        Assert.Equal(13000, run.Snapshot.Metrics.Run.DurationMs);
    }

    [Fact]
    public void Every_attempt_records_queue_wait_and_retry_wait_never_becomes_task_execution_time()
    {
        var run = Running();
        Task(run, ExecutionStatus.Ready, 1);
        Task(run, ExecutionStatus.Dispatched, 3);
        Assert.Equal(2000, Assert.Single(run.Snapshot.Metrics!.Tasks["node/task"].Attempts).QueueWaitMs);
        Task(run, ExecutionStatus.Running, 5);
        Apply(run, ExecutionStatus.Retrying, 12, endedAt: 8, outcome: ExecutionStatus.TimedOut);
        Assert.Equal(0, run.Snapshot.Metrics!.Tasks["node/task"].RetryCount);
        Task(run, ExecutionStatus.Dispatched, 20);
        Assert.Equal(12000, run.Snapshot.Metrics!.Tasks["node/task"].Attempts[1].QueueWaitMs);
        Task(run, ExecutionStatus.Running, 23);
        Task(run, ExecutionStatus.Succeeded, 29);
        var task = run.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Equal(1, task.RetryCount);
        Assert.Equal(19000, task.QueueWaitMs);
        Assert.Equal(9000, task.DurationMs);
        Assert.Equal(ExecutionStatus.TimedOut, task.Attempts[0].TerminalStatus);
        Assert.Equal(At(8), task.Attempts[0].EndedAt);
        Assert.Equal(ExecutionStatus.Succeeded, task.Attempts[1].TerminalStatus);
        Assert.Equal(1, run.Snapshot.Metrics.Nodes["node"].RetryCount);
        Assert.Equal(1, run.Snapshot.Metrics.Run.RetryCount);
    }

    [Theory]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.TimedOut)]
    [InlineData(ExecutionStatus.Cancelled)]
    public void Prestart_completion_records_wait_without_fabricating_start_or_duration(ExecutionStatus outcome)
    {
        var run = Running();
        Task(run, ExecutionStatus.Ready, 1);
        Task(run, ExecutionStatus.Dispatched, 3);
        Task(run, outcome, 11);
        var task = run.Snapshot.Metrics!.Tasks["node/task"];
        var attempt = Assert.Single(task.Attempts);
        Assert.Null(attempt.StartedAt);
        Assert.Null(task.StartedAt);
        Assert.Equal(10000, task.QueueWaitMs);
        Assert.Equal(10000, attempt.QueueWaitMs);
        Assert.Equal(0, task.DurationMs);
        Assert.Equal(outcome, attempt.TerminalStatus);
        Assert.Equal((int)outcome, task.TerminalStatus);
        Assert.Equal(0, task.RetryCount);
    }

    [Fact]
    public void Scheduled_retry_cancelled_before_redispatch_does_not_count_an_unexecuted_retry()
    {
        var run = Running();
        Task(run, ExecutionStatus.Ready, 1);
        Task(run, ExecutionStatus.Dispatched, 2);
        Task(run, ExecutionStatus.Running, 3);
        Task(run, ExecutionStatus.Retrying, 4);
        Task(run, ExecutionStatus.Cancelled, 30);
        var task = run.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Single(task.Attempts);
        Assert.Equal(0, task.RetryCount);
        Assert.Equal(1000, task.DurationMs);
        Assert.Equal(ExecutionStatus.Failed, task.Attempts[0].TerminalStatus);
        Assert.Equal((int)ExecutionStatus.Cancelled, task.TerminalStatus);
    }

    [Theory]
    [InlineData("$init")]
    [InlineData("$pre")]
    [InlineData("$post")]
    [InlineData("$sys-post")]
    [InlineData("node")]
    public void Stages_and_nodes_share_lifecycle_metrics(string key)
    {
        var run = NewRun(key);
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, At(1));
        foreach (var (status, at) in new[] { (ExecutionStatus.Ready, 2), (ExecutionStatus.Dispatched, 4),
                     (ExecutionStatus.Running, 7), (ExecutionStatus.SoftFailed, 12) })
            run.Append(EventEntity.Node, key, (int)status, At(at));
        var metric = run.Snapshot.Metrics!.Nodes[key];
        Assert.Equal(5000, metric.QueueWaitMs);
        Assert.Equal(5000, metric.DurationMs);
        Assert.Equal(At(7), metric.StartedAt);
        Assert.Equal(At(12), metric.EndedAt);
        Assert.Equal((int)ExecutionStatus.SoftFailed, metric.TerminalStatus);
    }

    [Theory]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Skipped)]
    [InlineData(ExecutionStatus.Reused)]
    public void Never_dispatched_terminal_entities_have_zero_attempts_and_execution_time(ExecutionStatus status)
    {
        var run = Running();
        Task(run, status, 10);
        run.Append(EventEntity.Node, "node", (int)status, At(10));
        var task = run.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Empty(task.Attempts);
        Assert.Equal(0, task.DurationMs);
        Assert.Equal(0, task.QueueWaitMs);
        Assert.Equal((int)status, task.TerminalStatus);
        Assert.Null(task.StartedAt);
    }

    [Fact]
    public void Queued_abort_records_queue_time_without_execution()
    {
        var run = NewRun();
        run.Append(EventEntity.Run, null, (int)RunStatus.Aborted, At(9));
        var metric = run.Snapshot.Metrics!.Run;
        Assert.Equal(9000, metric.QueueWaitMs);
        Assert.Equal(0, metric.DurationMs);
        Assert.Null(metric.StartedAt);
        Assert.Equal((int)RunStatus.Aborted, metric.TerminalStatus);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.TimedOut)]
    [InlineData(RunStatus.Aborted)]
    public void All_run_terminal_outcomes_record_start_end_and_duration(RunStatus status)
    {
        var run = Running();
        if (status == RunStatus.Aborted)
            run.Append(EventEntity.Run, null, (int)RunStatus.Aborting, At(10));
        run.Append(EventEntity.Run, null, (int)status, At(15));
        var metric = run.Snapshot.Metrics!.Run;
        Assert.Equal(At(0), metric.StartedAt);
        Assert.Equal(At(15), metric.EndedAt);
        Assert.Equal(15000, metric.DurationMs);
        Assert.Equal((int)status, metric.TerminalStatus);
    }

    [Fact]
    public void Checkpoint_plus_tail_and_serialized_snapshot_replay_identically()
    {
        var run = Running();
        Task(run, ExecutionStatus.Ready, 1);
        Task(run, ExecutionStatus.Dispatched, 2);
        Task(run, ExecutionStatus.Running, 3);
        var checkpoint = JsonSerializer.Deserialize<RunSnapshot>(JsonSerializer.Serialize(run.Snapshot))!;
        Task(run, ExecutionStatus.Retrying, 4);
        Task(run, ExecutionStatus.Dispatched, 8);
        Task(run, ExecutionStatus.Cancelled, 10);
        var restored = RunState.Restore(checkpoint, run.Events.Where(e => e.Sequence > checkpoint.LastSequence), Assert.Fail);
        AssertMetrics(run.Snapshot.Metrics!, restored.Snapshot.Metrics!);
    }

    [Fact]
    public void Reused_initial_rerun_entities_preserve_terminal_status_without_fabricating_execution()
    {
        var snapshot = NewRun().Snapshot with
        {
            Metrics = null,
            Nodes = ImmutableDictionary<string, ExecutionStatus>.Empty.Add("node", ExecutionStatus.Reused),
            Tasks = ImmutableDictionary<string, ExecutionStatus>.Empty.Add("node/task", ExecutionStatus.Reused)
        };
        var restored = RunState.Restore(snapshot, [], Assert.Fail);
        Assert.Equal((int)ExecutionStatus.Reused, restored.Snapshot.Metrics!.Nodes["node"].TerminalStatus);
        Assert.Equal((int)ExecutionStatus.Reused, restored.Snapshot.Metrics.Tasks["node/task"].TerminalStatus);
        Assert.Empty(restored.Snapshot.Metrics.Tasks["node/task"].Attempts);
        Assert.Null(restored.Snapshot.Metrics.Nodes["node"].StartedAt);
        Assert.Null(restored.Snapshot.Metrics.Nodes["node"].EndedAt);
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded)]
    [InlineData(ExecutionStatus.SoftFailed)]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.TimedOut)]
    [InlineData(ExecutionStatus.Cancelled)]
    public void Every_executed_terminal_task_status_is_recorded(ExecutionStatus status)
    {
        var run = Running();
        Task(run, ExecutionStatus.Ready, 1);
        Task(run, ExecutionStatus.Dispatched, 2);
        Task(run, ExecutionStatus.Running, 3);
        Task(run, status, 8);
        var metric = run.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Equal((int)status, metric.TerminalStatus);
        Assert.Equal(status, Assert.Single(metric.Attempts).TerminalStatus);
        Assert.Equal(5000, metric.DurationMs);
    }

    [Fact]
    public void Explicit_durable_attempt_identity_is_preserved_without_inventing_retries()
    {
        var run = Running();
        Task(run, ExecutionStatus.Ready, 1);
        run.Apply(run.CreateEvent(EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, At(2)) with { Attempt = 5 });
        var snapshot = run.Snapshot;
        Assert.Throws<InvalidOperationException>(() => run.Apply(
            run.CreateEvent(EventEntity.Task, "node/task", (int)ExecutionStatus.Running, At(3)) with { Attempt = 4 }));
        Assert.Same(snapshot, run.Snapshot);
        Task(run, ExecutionStatus.Running, 3);
        Task(run, ExecutionStatus.Retrying, 4);
        Task(run, ExecutionStatus.Dispatched, 8);
        var metric = run.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Equal([5, 6], metric.Attempts.Select(a => a.Attempt));
        Assert.Equal(1, metric.RetryCount);
    }

    [Theory]
    [InlineData(ExecutionStatus.Pending, 5)]
    [InlineData(ExecutionStatus.Running, 5)]
    [InlineData(ExecutionStatus.Failed, 20)]
    [InlineData(ExecutionStatus.Failed, 0)]
    public void Invalid_attempt_metadata_cannot_mutate_metrics(ExecutionStatus outcome, int endedAt)
    {
        var run = Running();
        Task(run, ExecutionStatus.Ready, 1);
        Task(run, ExecutionStatus.Dispatched, 2);
        var snapshot = run.Snapshot;
        var change = run.CreateEvent(EventEntity.Task, "node/task", (int)ExecutionStatus.Failed, At(10)) with
            { AttemptStatus = outcome, AttemptEndedAt = At(endedAt) };
        Assert.Throws<InvalidOperationException>(() => run.Apply(change));
        Assert.Same(snapshot, run.Snapshot);
    }

    private static void AssertMetrics(RunMetrics expected, RunMetrics actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static RunState NewRun(string key = "node") => new(Guid.CreateVersion7(), [new(key, [], [new("task")])],
        new("test", ImmutableSortedDictionary<string, string>.Empty), _ => { }, queuedAt: Epoch);

    private static RunState Running()
    {
        var run = NewRun();
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, At(0));
        return run;
    }

    private static DateTimeOffset At(int seconds) => Epoch.AddSeconds(seconds);
    private static void Task(RunState run, ExecutionStatus status, int at) =>
        run.Append(EventEntity.Task, "node/task", (int)status, At(at));
    private static void Apply(RunState run, ExecutionStatus status, int at, int endedAt, ExecutionStatus outcome) =>
        run.Apply(run.CreateEvent(EventEntity.Task, "node/task", (int)status, At(at)) with
            { AttemptEndedAt = At(endedAt), AttemptStatus = outcome });
}
