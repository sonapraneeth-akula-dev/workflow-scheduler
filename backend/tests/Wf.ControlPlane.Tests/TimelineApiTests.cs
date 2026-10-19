using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Tests;

public sealed class TimelineApiTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static RunState New() => new(Guid.CreateVersion7(),
        [new("$init", [], [new("run")]), new("node", [], [new("task"), new("unstarted")])],
        new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail, Epoch,
        new Dictionary<string, JsonElement> { ["private-variable"] = JsonSerializer.SerializeToElement("never-expose") });

    [Theory]
    [InlineData(ExecutionStatus.Pending)]
    [InlineData(ExecutionStatus.Ready)]
    [InlineData(ExecutionStatus.Skipped)]
    [InlineData(ExecutionStatus.Reused)]
    [InlineData(ExecutionStatus.Cancelled)]
    public void Timeline_never_invents_attempts_for_undispatched_work(ExecutionStatus status)
    {
        var run = New();
        if (status != ExecutionStatus.Pending)
            run.Append(EventEntity.Task, "node/task", (int)status, Epoch);
        Assert.Empty(RunTimelineResponse.From(run.Snapshot, new Dictionary<(string, int), string?>()).Attempts);
    }

    [Fact]
    public void Timeline_retains_exact_attempt_identity_agent_and_recorded_times_without_current_agent_guess()
    {
        var run = New();
        Emit(ExecutionStatus.Ready, 1);
        Emit(ExecutionStatus.Dispatched, 2, 4);
        Emit(ExecutionStatus.Running, 3);
        run.Apply(run.CreateEvent(EventEntity.Task, "node/task", (int)ExecutionStatus.Retrying, Epoch.AddSeconds(5)) with
            { FailureCategory = FailureCategory.ResourceLimit, AttemptStatus = ExecutionStatus.Failed });
        Emit(ExecutionStatus.Dispatched, 8, 9);
        Emit(ExecutionStatus.Running, 9);
        Emit(ExecutionStatus.Succeeded, 12);
        var agents = new Dictionary<(string, int), string?>
        {
            [("node/task", 4)] = "original-agent", [("node/task", 9)] = "replacement-agent",
            [("node/task", 10)] = "not-an-attempt"
        };
        var resumedAt = Epoch.AddSeconds(4);
        var response = RunTimelineResponse.From(run.Snapshot, agents, resumedFrom:
            new Dictionary<(string, int), DateTimeOffset?> { [("node/task", 9)] = resumedAt });
        Assert.Equal(new[] { 4, 9 }, response.Attempts.Select(a => a.Attempt));
        Assert.Equal(new[] { "original-agent", "replacement-agent" }, response.Attempts.Select(a => a.AgentId));
        Assert.All(response.Attempts, a => { Assert.Equal("node", a.NodeKey); Assert.Equal("node/task", a.TaskKey); });
        Assert.Equal(Epoch.AddSeconds(1), response.Attempts[0].QueuedAt);
        Assert.Equal(Epoch.AddSeconds(2), response.Attempts[0].DispatchedAt);
        Assert.Equal(Epoch.AddSeconds(3), response.Attempts[0].StartedAt);
        Assert.Equal(Epoch.AddSeconds(5), response.Attempts[0].EndedAt);
        Assert.Equal(2000, response.Attempts[0].QueueWaitMs);
        Assert.Equal(2000, response.Attempts[0].DurationMs);
        Assert.Equal("Failed", response.Attempts[0].TerminalStatus);
        Assert.Equal("ResourceLimit", response.Attempts[0].FailureCategory);
        Assert.Equal("Succeeded", response.Attempts[1].TerminalStatus);
        Assert.Null(response.Attempts[1].FailureCategory);
        Assert.Null(response.Attempts[0].ResumedFrom);
        Assert.Equal(resumedAt, response.Attempts[1].ResumedFrom);

        void Emit(ExecutionStatus status, int seconds, int? attempt = null) =>
            run.Apply(run.CreateEvent(EventEntity.Task, "node/task", (int)status, Epoch.AddSeconds(seconds)) with
                { Attempt = attempt });
    }

    [Fact]
    public void Timeline_uses_manifest_order_including_reserved_stages_or_snapshot_enumeration_as_fallback()
    {
        var run = New();
        foreach (var key in new[] { "node/task", "$init/run" })
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched })
            run.Append(EventEntity.Task, key, (int)status, Epoch);
        var order = new[] { "$init/run", "node/unstarted", "node/task" };
        var ordered = RunTimelineResponse.From(run.Snapshot, new Dictionary<(string, int), string?>(), order);
        Assert.Equal(new[] { "$init/run", "node/task" }, ordered.Attempts.Select(a => a.TaskKey));
        Assert.Equal("$init", ordered.Attempts[0].NodeKey);
        var fallback = RunTimelineResponse.From(run.Snapshot, new Dictionary<(string, int), string?>());
        Assert.Equal(run.Snapshot.Tasks.Keys.Where(k => k != "node/unstarted"),
            fallback.Attempts.Select(a => a.TaskKey));
        Assert.Throws<InvalidDataException>(() => RunTimelineResponse.From(run.Snapshot,
            new Dictionary<(string, int), string?>(), ["node/task"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timeline_legacy_unknown_agent_and_active_duration_remain_explicit_recorded_facts(bool started)
    {
        var run = New();
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched })
            run.Append(EventEntity.Task, "node/task", (int)status, Epoch);
        if (started) run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Running, Epoch.AddSeconds(1));
        var attempt = Assert.Single(RunTimelineResponse.From(run.Snapshot,
            new Dictionary<(string, int), string?>()).Attempts);
        Assert.Null(attempt.AgentId);
        Assert.Null(attempt.ResumedFrom);
        Assert.Equal(started ? Epoch.AddSeconds(1) : (DateTimeOffset?)null, attempt.StartedAt);
        Assert.Null(attempt.EndedAt);
        Assert.Null(attempt.TerminalStatus);
        Assert.Null(attempt.FailureCategory);
        Assert.Equal(0, attempt.DurationMs);
    }

    [Fact]
    public void Timeline_reuses_authoritative_metrics_validation_instead_of_repairing_corruption()
    {
        var snapshot = New().Snapshot;
        Assert.Throws<InvalidDataException>(() => RunTimelineResponse.From(snapshot with { Metrics = null },
            new Dictionary<(string, int), string?>()));
        Assert.Throws<InvalidDataException>(() => RunTimelineResponse.From(snapshot with
        {
            Metrics = snapshot.Metrics! with { Run = snapshot.Metrics.Run with { DurationMs = 1 } }
        }, new Dictionary<(string, int), string?>()));
    }

    [Fact]
    public void Timeline_preserves_reversed_recorded_clocks_with_existing_clamped_metric_semantics()
    {
        var run = New();
        foreach (var (status, seconds) in new[] { (ExecutionStatus.Ready, 4), (ExecutionStatus.Dispatched, 3),
            (ExecutionStatus.Running, 2), (ExecutionStatus.Failed, 1) })
            run.Append(EventEntity.Task, "node/task", (int)status, Epoch.AddSeconds(seconds));
        var attempt = Assert.Single(RunTimelineResponse.From(run.Snapshot,
            new Dictionary<(string, int), string?>()).Attempts);
        Assert.Equal(Epoch.AddSeconds(4), attempt.QueuedAt);
        Assert.Equal(Epoch.AddSeconds(3), attempt.DispatchedAt);
        Assert.Equal(Epoch.AddSeconds(2), attempt.StartedAt);
        Assert.Equal(Epoch.AddSeconds(1), attempt.EndedAt);
        Assert.Equal(0, attempt.QueueWaitMs);
        Assert.Equal(0, attempt.DurationMs);
    }

    [Fact]
    public void Timeline_source_generation_keeps_required_array_and_exact_safe_camelcase_contract()
    {
        var run = New();
        run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, Epoch);
        run.Append(EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, Epoch);
        var response = RunTimelineResponse.From(run.Snapshot, new Dictionary<(string, int), string?>());
        var text = JsonSerializer.Serialize(response, RunTimelineJsonContext.Default.RunTimelineResponse);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "attempts", "failures", "failureSummary", "lastSeq", "runId", "status" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(new[] { "agentId", "attempt", "dispatchedAt", "durationMs", "endedAt", "failureCategory",
            "nodeKey", "queueWaitMs", "queuedAt", "resumedFrom", "startedAt", "taskKey", "terminalStatus" },
            json.RootElement.GetProperty("attempts")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("attempts")[0].GetProperty("agentId").ValueKind);
        foreach (var secret in new[] { "never-expose", "variables", "metadata", "outputs", "error", "capabilities" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        var empty = JsonSerializer.Serialize(RunTimelineResponse.From(New().Snapshot,
            new Dictionary<(string, int), string?>()), RunTimelineJsonContext.Default.RunTimelineResponse);
        Assert.Contains("\"attempts\":[]", empty);
    }
}
