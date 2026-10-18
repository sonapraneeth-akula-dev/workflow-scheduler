using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Resources;
using Wf.Engine.Core.Metrics;

namespace Wf.Engine.Core.Tests;

public sealed class WorkflowTelemetryTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Completion_emits_seconds_outcomes_retries_and_every_attempt_without_sensitive_or_unbounded_labels()
    {
        using var probe = new Probe();
        var snapshot = Snapshot();
        WorkflowTelemetry.RecordCompletion(probe.Identity, snapshot);
        Assert.Equal(12, probe.Single("wf.run.duration").Value);
        Assert.Equal(3, probe.Single("wf.run.queue.duration").Value);
        Assert.Equal(1, probe.Single("wf.run.completed").Value);
        Assert.Equal("Succeeded", probe.Single("wf.run.completed").Tags["status"]);
        Assert.Equal(1, probe.Single("wf.run.retries").Value);
        Assert.Equal(6, probe.Single("wf.task.duration").Value);
        Assert.Equal(4, probe.Single("wf.task.queue.duration").Value);
        Assert.Equal(1, probe.Single("wf.task.retries").Value);
        Assert.Equal([2d, 4d], probe.Values("wf.task.attempt.duration").Select(value => value.Value));
        Assert.Equal(["Failed", "Succeeded"], probe.Values("wf.task.attempt.completed").Select(value => value.Tags["status"]));
        Assert.Equal("TaskError", probe.Values("wf.task.attempt.completed")[0].Tags["failure_category"]);
        Assert.All(probe.All, value =>
        {
            Assert.All(value.Tags.Keys, key => Assert.Contains(key,
                new[] { "team", "type", "workflow", "node", "task", "status", "failure_category" }));
            Assert.DoesNotContain(snapshot.RunId.ToString(), value.Tags.Values);
            Assert.DoesNotContain("private", value.Tags.Values);
        });
        Assert.Equal("s", probe.Single("wf.run.duration").Unit);
    }

    [Fact]
    public void Nonstarted_skipped_and_reused_work_does_not_acquire_execution_duration_or_attempts()
    {
        using var probe = new Probe();
        var metric = new EntityMetrics(Epoch, EndedAt: Epoch.AddSeconds(7), QueueWaitMs: 7000,
            TerminalStatus: (int)ExecutionStatus.Skipped);
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Metrics = snapshot.Metrics! with
            {
                Tasks = snapshot.Metrics.Tasks.SetItem("node/task", metric),
                Nodes = snapshot.Metrics.Nodes.SetItem("node", metric)
            },
            Tasks = snapshot.Tasks.SetItem("node/task", ExecutionStatus.Skipped),
            Nodes = snapshot.Nodes.SetItem("node", ExecutionStatus.Skipped)
        };
        WorkflowTelemetry.RecordCompletion(probe.Identity, snapshot);
        Assert.Empty(probe.Values("wf.task.duration"));
        Assert.Empty(probe.Values("wf.node.duration"));
        Assert.Empty(probe.Values("wf.task.attempt.completed"));
        Assert.Equal(7, probe.Single("wf.task.queue.duration").Value);
        Assert.Equal("Skipped", probe.Single("wf.task.completed").Tags["status"]);
    }

    [Fact]
    public void Resource_summaries_emit_actual_measurements_and_coverage_not_zeroes_for_unavailable_values()
    {
        using var probe = new Probe();
        ResourceAttemptSummary summary = new("node/task", 1, Guid.NewGuid(), 2, 25, 40, 4096, 100, 200,
            1.5, 1, null, null, null);
        WorkflowTelemetry.RecordCompletion(probe.Identity, Snapshot(), [summary,
            summary with { AverageCpuPercent = null, MaxCpuPercent = null, PeakRamBytes = null,
                NetworkSentBytes = null, NetworkReceivedBytes = null }]);
        Assert.Equal(25, probe.Single("wf.task.cpu.average").Value);
        Assert.Equal(40, probe.Single("wf.task.cpu.max").Value);
        Assert.Equal(4096, probe.Single("wf.task.memory.peak").Value);
        Assert.Equal(100, probe.Single("wf.task.network.sent").Value);
        Assert.Equal(200, probe.Single("wf.task.network.received").Value);
        Assert.Equal([1.5, 1.5], probe.Values("wf.task.cpu.observed.duration").Select(value => value.Value));
        Assert.Equal("By", probe.Single("wf.task.memory.peak").Unit);
        Assert.All(probe.Values("wf.task.cpu.average"), value => Assert.DoesNotContain("attempt", value.Tags.Keys));
    }

    [Fact]
    public void A_terminal_run_does_not_turn_unfinished_children_into_completed_work()
    {
        using var probe = new Probe();
        var snapshot = Snapshot();
        WorkflowTelemetry.RecordCompletion(probe.Identity, snapshot with
        {
            Status = RunStatus.Aborted,
            Nodes = snapshot.Nodes.SetItem("node", ExecutionStatus.Pending),
            Tasks = snapshot.Tasks.SetItem("node/task", ExecutionStatus.Pending)
        });
        Assert.Equal("Aborted", probe.Single("wf.run.completed").Tags["status"]);
        Assert.Empty(probe.Values("wf.node.completed"));
        Assert.Empty(probe.Values("wf.task.completed"));
        Assert.Empty(probe.Values("wf.task.attempt.completed"));
    }

    [Fact]
    public void Applying_or_replaying_a_terminal_event_never_emits_and_active_completion_is_rejected()
    {
        using var probe = new Probe();
        var run = new RunState(Guid.CreateVersion7(), [], new("test", ImmutableSortedDictionary<string, string>.Empty),
            Assert.Fail, Epoch);
        var initial = run.Snapshot;
        Assert.Throws<ArgumentException>(() => WorkflowTelemetry.RecordCompletion(probe.Identity, initial));
        run.Append(Wf.Contracts.Events.EventEntity.Run, null, (int)RunStatus.Running, Epoch);
        run.Append(Wf.Contracts.Events.EventEntity.Run, null, (int)RunStatus.Succeeded, Epoch.AddSeconds(3));
        RunState.Restore(initial, run.Events, Assert.Fail);
        Assert.Empty(probe.All);
        WorkflowTelemetry.RecordCompletion(probe.Identity, run.Snapshot);
        Assert.Equal(3, probe.Single("wf.run.duration").Value);
    }

    private static RunSnapshot Snapshot()
    {
        var metric = new EntityMetrics(Epoch, Epoch.AddSeconds(3), Epoch.AddSeconds(15), 3000, 12000, 1,
            (int)RunStatus.Succeeded);
        var task = metric with
        {
            TerminalStatus = (int)ExecutionStatus.Succeeded, DurationMs = 6000, QueueWaitMs = 4000,
            Attempts =
            [
                new(1, Epoch, Epoch.AddSeconds(1), Epoch.AddSeconds(2), Epoch.AddSeconds(4), 2000, 2000,
                    ExecutionStatus.Failed) { FailureCategory = FailureCategory.TaskError },
                new(2, Epoch.AddSeconds(6), Epoch.AddSeconds(7), Epoch.AddSeconds(8), Epoch.AddSeconds(12),
                    2000, 4000, ExecutionStatus.Succeeded)
            ]
        };
        return new(Guid.CreateVersion7(), RunStatus.Succeeded, 10,
            ImmutableDictionary<string, ExecutionStatus>.Empty.Add("node", ExecutionStatus.Succeeded),
            ImmutableDictionary<string, ExecutionStatus>.Empty.Add("node/task", ExecutionStatus.Succeeded),
            new("type", ImmutableSortedDictionary<string, string>.Empty.Add("private", "private")),
            ImmutableDictionary<string, JsonElement>.Empty.Add("private", JsonSerializer.SerializeToElement("private")))
        {
            Metrics = new(metric, ImmutableDictionary<string, EntityMetrics>.Empty.Add("node", metric),
                ImmutableDictionary<string, EntityMetrics>.Empty.Add("node/task", task))
        };
    }

    private sealed record MetricValue(string Name, double Value, string? Unit, Dictionary<string, string> Tags);

    private sealed class Probe : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<MetricValue> values = new();
        public WorkflowMetricIdentity Identity { get; } = new("team", "type", Guid.NewGuid().ToString("N"));
        public MetricValue[] All => values.ToArray();
        public MetricValue[] Values(string name) => All.Where(value => value.Name == name).ToArray();
        public MetricValue Single(string name) => Assert.Single(Values(name));

        public Probe()
        {
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == WorkflowTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.Start();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var attributes = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? "");
            if (attributes.GetValueOrDefault("workflow") == Identity.Workflow)
                values.Enqueue(new(instrument.Name, value, instrument.Unit, attributes));
        }

        public void Dispose() => listener.Dispose();
    }
}
