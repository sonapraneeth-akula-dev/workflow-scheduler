using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api;
using Wf.Engine.Core;
using Wf.Engine.Core.Metrics;

namespace Wf.ControlPlane.Tests;

public sealed class MetricsApiTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static RunState New() => new(Guid.CreateVersion7(),
        [new("$init", [], [new("run")]), new("node", [], [new("task")])],
        new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail, Epoch,
        new Dictionary<string, JsonElement> { ["secret"] = JsonSerializer.SerializeToElement("never-expose") });

    [Theory]
    [InlineData(ExecutionStatus.Pending)]
    [InlineData(ExecutionStatus.Skipped)]
    [InlineData(ExecutionStatus.Reused)]
    [InlineData(ExecutionStatus.Cancelled)]
    public void Never_started_entities_have_no_fabricated_attempts_or_start(ExecutionStatus status)
    {
        var run = New();
        if (status != ExecutionStatus.Pending)
        {
            run.Append(EventEntity.Task, "node/task", (int)status, Epoch.AddSeconds(1));
            run.Append(EventEntity.Node, "node", (int)status, Epoch.AddSeconds(1));
        }
        var response = RunMetricsResponse.From(run.Snapshot);
        Assert.Equal(status.ToString(), response.Nodes["node"].Status);
        Assert.Equal(status.ToString(), response.Tasks["node/task"].Status);
        Assert.Null(response.Tasks["node/task"].StartedAt);
        Assert.Equal(0, response.Tasks["node/task"].DurationMs);
        Assert.Empty(response.Tasks["node/task"].Attempts);
        Assert.Single(response.Stages);
        Assert.Single(response.Nodes);
    }

    [Fact]
    public void Retried_failure_categories_belong_to_attempts_and_do_not_contaminate_success()
    {
        var run = New();
        var initial = run.Snapshot;
        Emit(ExecutionStatus.Ready, 1);
        Emit(ExecutionStatus.Dispatched, 2);
        Emit(ExecutionStatus.Running, 3);
        run.Apply(run.CreateEvent(EventEntity.Task, "node/task", (int)ExecutionStatus.Retrying, Epoch.AddSeconds(5)) with
            { FailureCategory = FailureCategory.ResourceLimit, AttemptStatus = ExecutionStatus.Failed });
        Emit(ExecutionStatus.Dispatched, 8);
        Emit(ExecutionStatus.Running, 9);
        Emit(ExecutionStatus.Succeeded, 12);
        var metric = RunMetricsResponse.From(run.Snapshot).Tasks["node/task"];
        Assert.Equal(1, metric.RetryCount);
        Assert.Equal(6000, metric.QueueWaitMs);
        Assert.Equal(5000, metric.DurationMs);
        Assert.Equal("ResourceLimit", metric.Attempts[0].FailureCategory);
        Assert.Null(metric.Attempts[1].FailureCategory);
        Assert.Null(metric.FailureCategory);
        var replay = RunState.Restore(initial, run.Events, Assert.Fail);
        Assert.Equal(JsonSerializer.Serialize(run.Snapshot.Metrics), JsonSerializer.Serialize(replay.Snapshot.Metrics));

        void Emit(ExecutionStatus status, int seconds) =>
            run.Append(EventEntity.Task, "node/task", (int)status, Epoch.AddSeconds(seconds));
    }

    [Fact]
    public void Source_generated_contract_is_camelcase_and_excludes_private_snapshot_content()
    {
        var response = RunMetricsResponse.From(New().Snapshot);
        var text = JsonSerializer.Serialize(response, MetricsJsonContext.Default.RunMetricsResponse);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "lastSeq", "nodes", "run", "runId", "stages", "status", "tasks" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("never-expose", text);
        Assert.DoesNotContain("variables", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metadata", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Committed_clock_skew_preserves_the_recorders_clamped_nonnegative_times()
    {
        var run = New();
        foreach (var (status, seconds) in new[] { (ExecutionStatus.Ready, 1), (ExecutionStatus.Dispatched, 3),
                     (ExecutionStatus.Running, 2), (ExecutionStatus.Failed, 1) })
            run.Append(EventEntity.Task, "node/task", (int)status, Epoch.AddSeconds(seconds));
        var metric = RunMetricsResponse.From(run.Snapshot).Tasks["node/task"];
        Assert.Equal(1000, metric.QueueWaitMs);
        Assert.Equal(0, metric.DurationMs);
        Assert.Null(Assert.Single(metric.Attempts).FailureCategory);
    }

    [Theory]
    [InlineData(FailureCategory.TaskError, FailureCategory.TaskError)]
    [InlineData(FailureCategory.Placement, null)]
    [InlineData(null, null)]
    public void Parent_categories_require_unambiguous_explicit_failed_child_metadata(
        FailureCategory? second, FailureCategory? expected)
    {
        var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("one"), new("two")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail, Epoch);
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, Epoch);
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, Epoch);
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Dispatched, Epoch);
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Running, Epoch);
        foreach (var (key, category) in new[] { ("one", (FailureCategory?)FailureCategory.TaskError), ("two", second) })
        {
            foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                run.Append(EventEntity.Task, "node/" + key, (int)state, Epoch);
            run.Apply(run.CreateEvent(EventEntity.Task, "node/" + key, (int)ExecutionStatus.Failed, Epoch) with
                { FailureCategory = category });
        }
        run.Append(EventEntity.Node, "node", (int)ExecutionStatus.Failed, Epoch);
        run.Append(EventEntity.Run, null, (int)RunStatus.Failed, Epoch);
        var response = RunMetricsResponse.From(run.Snapshot);
        Assert.Equal(expected?.ToString(), response.Nodes["node"].FailureCategory);
        Assert.Equal(expected?.ToString(), response.Run.FailureCategory);
    }

    [Theory]
    [InlineData("missing-metrics")]
    [InlineData("missing-node")]
    [InlineData("extra-task")]
    [InlineData("negative")]
    [InlineData("nan")]
    [InlineData("status")]
    [InlineData("category")]
    [InlineData("duration")]
    [InlineData("retry-total")]
    public void Corrupt_metrics_are_rejected_not_replaced_with_zeros(string corruption)
    {
        var snapshot = New().Snapshot;
        var metrics = snapshot.Metrics!;
        metrics = corruption switch
        {
            "missing-node" => metrics with { Nodes = metrics.Nodes.Remove("node") },
            "extra-task" => metrics with { Tasks = metrics.Tasks.Add("node/unknown", new()) },
            "negative" => metrics with { Run = metrics.Run with { QueueWaitMs = -1 } },
            "nan" => metrics with { Run = metrics.Run with { DurationMs = double.NaN } },
            "status" => metrics with { Run = metrics.Run with { TerminalStatus = (int)RunStatus.Failed } },
            "category" => metrics with { Run = metrics.Run with { FailureCategory = (FailureCategory)999 } },
            "duration" => metrics with { Tasks = metrics.Tasks.SetItem("node/task", new(DurationMs: 1)) },
            "retry-total" => metrics with { Run = metrics.Run with { RetryCount = 1 } },
            _ => metrics
        };
        Assert.Throws<InvalidDataException>(() => RunMetricsResponse.From(snapshot with
            { Metrics = corruption == "missing-metrics" ? null : metrics }));
    }

    [Fact]
    public void Missing_persisted_timing_field_is_corruption_but_legacy_absent_category_is_unknown()
    {
        var metric = new EntityMetrics();
        var serialized = JsonSerializer.Serialize(metric);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EntityMetrics>(serialized.Replace("\"DurationMs\":0,", "")));
        Assert.Null(JsonSerializer.Deserialize<EntityMetrics>(serialized.Replace("\"FailureCategory\":null,", ""))!.FailureCategory);
    }

    [Theory]
    [InlineData(EventEntity.Run, null, (int)RunStatus.Running, FailureCategory.TaskError)]
    [InlineData(EventEntity.Node, "node", (int)ExecutionStatus.Ready, FailureCategory.TaskError)]
    [InlineData(EventEntity.Task, "node/task", (int)ExecutionStatus.Skipped, FailureCategory.TaskError)]
    [InlineData(EventEntity.Task, "node/task", (int)ExecutionStatus.Cancelled, (FailureCategory)999)]
    public void Invalid_failure_metadata_cannot_enter_committed_state(EventEntity entity, string? key, int state,
        FailureCategory category)
    {
        var run = RunState.Restore(New().Snapshot, [], _ => { });
        var change = new RunEvent(run.Snapshot.RunId, 1, Epoch, entity, key, state) { FailureCategory = category };
        Assert.Throws<InvalidOperationException>(() => run.Apply(change));
        Assert.Equal(0, run.Snapshot.LastSequence);
    }
}
