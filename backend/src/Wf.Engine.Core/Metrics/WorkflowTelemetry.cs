using System.Diagnostics;
using System.Diagnostics.Metrics;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Resources;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Metrics;

public sealed record WorkflowMetricIdentity(string Team, string Type, string Workflow)
{
    public static WorkflowMetricIdentity FromWorkflowId(string workflowId, string type)
    {
        var parts = workflowId.Split('/');
        return new(parts[0], parts.Length == 3 ? parts[1] : type, workflowId);
    }
}

/// <summary>Completion telemetry only. Call after commit, never while projecting or replaying events.</summary>
public static class WorkflowTelemetry
{
    public const string MeterName = "Wf.Runtime";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("wf.run.completed", "{run}");
    private static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>("wf.run.duration", "s");
    private static readonly Histogram<double> RunQueue = Meter.CreateHistogram<double>("wf.run.queue.duration", "s");
    private static readonly Counter<long> RunRetries = Meter.CreateCounter<long>("wf.run.retries", "{retry}");
    private static readonly Counter<long> Nodes = Meter.CreateCounter<long>("wf.node.completed", "{node}");
    private static readonly Histogram<double> NodeDuration = Meter.CreateHistogram<double>("wf.node.duration", "s");
    private static readonly Histogram<double> NodeQueue = Meter.CreateHistogram<double>("wf.node.queue.duration", "s");
    private static readonly Counter<long> Tasks = Meter.CreateCounter<long>("wf.task.completed", "{task}");
    private static readonly Histogram<double> TaskDuration = Meter.CreateHistogram<double>("wf.task.duration", "s");
    private static readonly Histogram<double> TaskQueue = Meter.CreateHistogram<double>("wf.task.queue.duration", "s");
    private static readonly Counter<long> TaskRetries = Meter.CreateCounter<long>("wf.task.retries", "{retry}");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("wf.task.attempt.completed", "{attempt}");
    private static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>("wf.task.attempt.duration", "s");
    private static readonly Histogram<double> AttemptQueue = Meter.CreateHistogram<double>("wf.task.attempt.queue.duration", "s");
    private static readonly Histogram<double> CpuAverage = Meter.CreateHistogram<double>("wf.task.cpu.average", "%");
    private static readonly Histogram<double> CpuMax = Meter.CreateHistogram<double>("wf.task.cpu.max", "%");
    private static readonly Histogram<double> MemoryPeak = Meter.CreateHistogram<double>("wf.task.memory.peak", "By");
    private static readonly Counter<double> Sent = Meter.CreateCounter<double>("wf.task.network.sent", "By");
    private static readonly Counter<double> Received = Meter.CreateCounter<double>("wf.task.network.received", "By");
    private static readonly Counter<long> Samples = Meter.CreateCounter<long>("wf.task.resource.samples", "{sample}");
    private static readonly Histogram<double> CpuCoverage = Meter.CreateHistogram<double>("wf.task.cpu.observed.duration", "s");
    private static readonly Histogram<double> NetworkCoverage = Meter.CreateHistogram<double>("wf.task.network.observed.duration", "s");

    public static bool Enabled => Runs.Enabled || RunDuration.Enabled || RunQueue.Enabled || RunRetries.Enabled ||
        Nodes.Enabled || NodeDuration.Enabled || NodeQueue.Enabled || Tasks.Enabled || TaskDuration.Enabled ||
        TaskQueue.Enabled || TaskRetries.Enabled || Attempts.Enabled || AttemptDuration.Enabled || AttemptQueue.Enabled ||
        CpuAverage.Enabled || CpuMax.Enabled || MemoryPeak.Enabled || Sent.Enabled || Received.Enabled ||
        Samples.Enabled || CpuCoverage.Enabled || NetworkCoverage.Enabled;

    public static TagList Tags(WorkflowMetricIdentity identity) => new()
    {
        { "team", identity.Team }, { "type", identity.Type }, { "workflow", identity.Workflow }
    };

    public static void RecordCompletion(WorkflowMetricIdentity identity, RunSnapshot snapshot,
        IReadOnlyList<ResourceAttemptSummary>? resources = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TransitionTable.IsTerminal(snapshot.Status))
            throw new ArgumentException("Only committed terminal snapshots can emit completion telemetry.", nameof(snapshot));
        var metrics = snapshot.Metrics ?? throw new ArgumentException("Completion requires projected metrics.", nameof(snapshot));
        if (!Enabled) return;
        var tags = Tags(identity);
        tags.Add("status", snapshot.Status.ToString());
        Category(ref tags, metrics.Run.FailureCategory);
        Runs.Add(1, tags);
        Duration(metrics.Run, RunDuration, RunQueue, tags);
        RunRetries.Add(metrics.Run.RetryCount, tags);
        foreach (var (key, metric) in metrics.Nodes)
        {
            if (!TransitionTable.IsTerminal(snapshot.Nodes[key])) continue;
            var node = EntityTags(identity, key, null, snapshot.Nodes[key].ToString(), metric.FailureCategory);
            Nodes.Add(1, node);
            Duration(metric, NodeDuration, NodeQueue, node);
        }
        foreach (var (key, metric) in metrics.Tasks)
        {
            if (!TransitionTable.IsTerminal(snapshot.Tasks[key])) continue;
            var split = key.IndexOf('/');
            var node = key[..split];
            var task = key[(split + 1)..];
            var entity = EntityTags(identity, node, task, snapshot.Tasks[key].ToString(), metric.FailureCategory);
            Tasks.Add(1, entity);
            Duration(metric, TaskDuration, TaskQueue, entity);
            TaskRetries.Add(metric.RetryCount, entity);
            foreach (var attempt in metric.Attempts)
            {
                if (attempt.TerminalStatus is not { } outcome) continue;
                var attemptTags = EntityTags(identity, node, task, outcome.ToString(), attempt.FailureCategory);
                Attempts.Add(1, attemptTags);
                AttemptQueue.Record(attempt.QueueWaitMs / 1000, attemptTags);
                if (attempt.StartedAt is not null) AttemptDuration.Record(attempt.DurationMs / 1000, attemptTags);
            }
        }
        if (resources is null) return;
        foreach (var resource in resources)
        {
            var split = resource.TaskKey.IndexOf('/');
            var resourceTags = Tags(identity);
            resourceTags.Add("node", resource.TaskKey[..split]);
            resourceTags.Add("task", resource.TaskKey[(split + 1)..]);
            Samples.Add(resource.SampleCount, resourceTags);
            if (resource.AverageCpuPercent is { } average) CpuAverage.Record(average, resourceTags);
            if (resource.MaxCpuPercent is { } max) CpuMax.Record(max, resourceTags);
            if (resource.PeakRamBytes is { } ram) MemoryPeak.Record((double)ram, resourceTags);
            if (resource.NetworkSentBytes is { } sent) Sent.Add((double)sent, resourceTags);
            if (resource.NetworkReceivedBytes is { } received) Received.Add((double)received, resourceTags);
            CpuCoverage.Record(resource.CpuObservedSeconds, resourceTags);
            NetworkCoverage.Record(resource.NetworkObservedSeconds, resourceTags);
        }
    }

    private static void Duration(EntityMetrics metric, Histogram<double> duration, Histogram<double> queue, TagList tags)
    {
        if (metric.StartedAt is not null) duration.Record(metric.DurationMs / 1000, tags);
        if (metric.QueuedAt is not null || !metric.Attempts.IsEmpty) queue.Record(metric.QueueWaitMs / 1000, tags);
    }

    private static TagList EntityTags(WorkflowMetricIdentity identity, string node, string? task, string status,
        FailureCategory? category)
    {
        var tags = Tags(identity);
        tags.Add("node", node);
        if (task is not null) tags.Add("task", task);
        tags.Add("status", status);
        Category(ref tags, category);
        return tags;
    }

    private static void Category(ref TagList tags, FailureCategory? category)
    {
        if (category is { } known) tags.Add("failure_category", known.ToString());
    }
}
