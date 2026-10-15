using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core;
using Wf.Engine.Core.StateMachine;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Testing;

/// <summary>Framework-independent assertion failure, with the expected and observed execution evidence.</summary>
public sealed class RunAssertionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Assertions preserve the log for fluent chaining and validate its event history against its snapshot.</summary>
public static class RunAssertions
{
    public static DryRunLog AssertRunStatus(this DryRunLog log, RunStatus expected)
    {
        if (!Enum.IsDefined(expected))
            throw new ArgumentOutOfRangeException(nameof(expected));
        Validate(log);
        Equal("Run status", expected, log.Snapshot.Status);
        return log;
    }

    public static DryRunLog AssertNodeStatus(this DryRunLog log, string nodeKey, ExecutionStatus expected) =>
        AssertStatus(log, EventEntity.Node, nodeKey, expected);

    public static DryRunLog AssertTaskStatus(this DryRunLog log, string taskKey, ExecutionStatus expected) =>
        AssertStatus(log, EventEntity.Task, taskKey, expected);

    public static DryRunLog AssertSkipped(this DryRunLog log, params string[] nodeKeys)
    {
        Keys(nodeKeys);
        foreach (var key in nodeKeys)
        {
            log.AssertNodeStatus(key, ExecutionStatus.Skipped);
            foreach (var task in log.Snapshot.Tasks.Keys.Where(task => task.StartsWith(key + "/", StringComparison.Ordinal)))
            {
                log.AssertTaskStatus(task, ExecutionStatus.Skipped);
                log.AssertRetries(task, 0);
                if (log.Attempts.Any(attempt => attempt.TaskKey == task))
                    throw new RunAssertionException($"Skipped node '{key}' unexpectedly executed task '{task}'.");
            }
        }
        return log;
    }

    /// <summary>Asserts a relative, non-overlapping execution order, not declaration or timestamp order.
    /// Other nodes may intervene; each named node must execute and finish before the next starts.</summary>
    public static DryRunLog AssertNodeOrder(this DryRunLog log, params string[] nodeKeys)
    {
        Keys(nodeKeys);
        Validate(log);
        long previousEnd = 0;
        string? previousKey = null;
        foreach (var key in nodeKeys)
        {
            RequireKey(log, EventEntity.Node, key);
            var events = Events(log, EventEntity.Node, key);
            var start = events.SingleOrDefault(e => e.State == (int)ExecutionStatus.Running) ??
                throw new RunAssertionException($"Node '{key}' did not execute; actual status {log.Snapshot.Nodes[key]}.");
            var end = events.LastOrDefault(e => TransitionTable.IsTerminal((ExecutionStatus)e.State)) ??
                throw new RunAssertionException($"Node '{key}' has no terminal event.");
            if (start.Sequence <= previousEnd)
                throw new RunAssertionException($"Expected node '{previousKey}' to finish before '{key}' starts; " +
                    $"completion sequence {previousEnd}, start sequence {start.Sequence}.");
            previousEnd = end.Sequence;
            previousKey = key;
        }
        return log;
    }

    /// <summary>Checks actual attempts, contiguous attempt numbers and their Running/Retrying/terminal events.
    /// Retries count dispatched additional attempts, not merely a scheduled retry or a snapshot summary.</summary>
    public static DryRunLog AssertRetries(this DryRunLog log, string taskKey, int expected)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expected);
        Validate(log);
        RequireKey(log, EventEntity.Task, taskKey);
        var attempts = log.Attempts.Where(a => a.TaskKey == taskKey).ToArray();
        var events = Events(log, EventEntity.Task, taskKey);
        var retries = events.Count(e => e.State == (int)ExecutionStatus.Retrying);
        Equal($"Task '{taskKey}' retry events", expected, retries);
        Equal($"Task '{taskKey}' executed retries", expected, Math.Max(0, attempts.Length - 1));
        if (attempts.Length == 0)
        {
            if (events.Any(e => e.State is (int)ExecutionStatus.Running or (int)ExecutionStatus.Dispatched or (int)ExecutionStatus.Retrying))
                throw new RunAssertionException($"Task '{taskKey}' has execution events but no persisted attempts.");
            return log;
        }
        var states = new List<ExecutionStatus> { ExecutionStatus.Ready };
        foreach (var attempt in attempts)
        {
            states.Add(ExecutionStatus.Dispatched);
            states.Add(ExecutionStatus.Running);
            states.Add(attempt == attempts[^1] ? log.Snapshot.Tasks[taskKey] : ExecutionStatus.Retrying);
        }
        Sequence($"Task '{taskKey}' attempt events", states, events.Select(e => (ExecutionStatus)e.State));
        for (var i = 0; i < attempts.Length; i++)
        {
            var attempt = attempts[i];
            Equal($"Task '{taskKey}' persisted attempt number at index {i}", i + 1, attempt.Attempt);
            var start = events[2 + 3 * i];
            var end = events[3 + 3 * i];
            if (attempt.StartedAt != start.At || attempt.EndedAt != end.At || attempt.EndedAt < attempt.StartedAt ||
                (i > 0 && attempt.StartedAt < attempts[i - 1].EndedAt))
                throw new RunAssertionException($"Task '{taskKey}' attempt {attempt.Attempt} timestamps " +
                    $"[{attempt.StartedAt:O}, {attempt.EndedAt:O}] disagree with events {start.Sequence}/{end.Sequence}.");
            if (!TransitionTable.IsTerminal(attempt.Status) ||
                (i < attempts.Length - 1 && (attempt.Status is not
                    (ExecutionStatus.Failed or ExecutionStatus.SoftFailed or ExecutionStatus.TimedOut) || attempt.Failure is null)))
                throw new RunAssertionException($"Task '{taskKey}' attempt {attempt.Attempt} has invalid retry outcome {attempt.Status}.");
        }
        Equal($"Task '{taskKey}' final persisted attempt status", log.Snapshot.Tasks[taskKey], attempts[^1].Status);
        return log;
    }

    public static DryRunLog AssertAttemptStatuses(this DryRunLog log, string taskKey, params ExecutionStatus[] expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Any(status => !Enum.IsDefined(status)))
            throw new ArgumentOutOfRangeException(nameof(expected));
        log.AssertRetries(taskKey, Math.Max(0, expected.Length - 1));
        Sequence($"Task '{taskKey}' persisted attempt statuses", expected,
            log.Attempts.Where(a => a.TaskKey == taskKey).Select(a => a.Status));
        return log;
    }

    public static DryRunLog AssertStageStatus(this DryRunLog log, string stageKey,
        ExecutionStatus expected = ExecutionStatus.Succeeded)
    {
        Stage(stageKey);
        return log.AssertNodeStatus(stageKey, expected);
    }

    public static DryRunLog AssertStageOrder(this DryRunLog log, params string[] stageKeys)
    {
        Keys(stageKeys);
        foreach (var key in stageKeys)
            Stage(key);
        return log.AssertNodeOrder(stageKeys);
    }

    /// <summary>Compares JSON structurally, including value kinds; outputs are node-scoped, not task-scoped.</summary>
    public static DryRunLog AssertOutput(this DryRunLog log, string nodeKey, string outputKey, JsonElement expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputKey);
        if (expected.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("An expected output must be a defined JSON value.", nameof(expected));
        Validate(log);
        RequireKey(log, EventEntity.Node, nodeKey);
        if (!log.Outputs.TryGetValue(nodeKey, out var outputs) || !outputs.TryGetValue(outputKey, out var actual))
            throw new RunAssertionException($"Node '{nodeKey}' is missing output '{outputKey}'. Available outputs: " +
                $"[{string.Join(", ", (outputs?.Keys ?? []).Order(StringComparer.Ordinal))}].");
        if (actual.ValueKind == JsonValueKind.Undefined || !JsonElement.DeepEquals(expected, actual))
            throw new RunAssertionException($"Output '{nodeKey}/{outputKey}': expected {Json(expected)}, actual {Json(actual)}.");
        return log;
    }

    public static DryRunLog AssertNoOutputs(this DryRunLog log, string nodeKey)
    {
        Validate(log);
        RequireKey(log, EventEntity.Node, nodeKey);
        if (!log.Outputs.TryGetValue(nodeKey, out var outputs))
            throw new RunAssertionException($"Node '{nodeKey}' has no persisted output collection.");
        if (outputs.Count != 0)
            throw new RunAssertionException($"Node '{nodeKey}' expected no outputs; actual [{string.Join(", ", outputs.Keys.Order(StringComparer.Ordinal))}].");
        return log;
    }

    private static DryRunLog AssertStatus(DryRunLog log, EventEntity entity, string key, ExecutionStatus expected)
    {
        if (!Enum.IsDefined(expected))
            throw new ArgumentOutOfRangeException(nameof(expected));
        Validate(log);
        RequireKey(log, entity, key);
        Equal($"{entity} '{key}' status", expected,
            (entity == EventEntity.Node ? log.Snapshot.Nodes : log.Snapshot.Tasks)[key]);
        return log;
    }

    private static void Validate(DryRunLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Snapshot is null || log.Events.IsDefault || log.Attempts.IsDefault || log.Outputs is null)
            throw new RunAssertionException("Run log requires a snapshot, initialized events, attempts and outputs.");
        try
        {
            var snapshot = log.Snapshot;
            var replay = RunState.Restore(snapshot with
            {
                Status = RunStatus.Queued, LastSequence = 0, Abort = null, EndedAt = null, AbortTargets = [],
                Nodes = snapshot.Nodes.ToImmutableDictionary(p => p.Key, _ => ExecutionStatus.Pending, StringComparer.Ordinal),
                Tasks = snapshot.Tasks.ToImmutableDictionary(p => p.Key, _ => ExecutionStatus.Pending, StringComparer.Ordinal)
            }, log.Events, _ => { }).Snapshot;
            Equal("Run event/snapshot status", snapshot.Status, replay.Status);
            Equal("Run event/snapshot last sequence", snapshot.LastSequence, replay.LastSequence);
            Equal("Run event/snapshot end time", snapshot.EndedAt, replay.EndedAt);
            foreach (var (key, state) in snapshot.Nodes)
                Equal($"Node '{key}' event/snapshot status", state, replay.Nodes[key]);
            foreach (var (key, state) in snapshot.Tasks)
                Equal($"Task '{key}' event/snapshot status", state, replay.Tasks[key]);
            foreach (var attempt in log.Attempts)
                RequireKey(log, EventEntity.Task, attempt.TaskKey);
            foreach (var key in log.Outputs.Keys)
                RequireKey(log, EventEntity.Node, key);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            throw new RunAssertionException($"Invalid persisted event log: {error.Message}", error);
        }
    }

    private static RunEvent[] Events(DryRunLog log, EventEntity entity, string key) =>
        log.Events.Where(e => e.Entity == entity && e.Key == key).ToArray();

    private static void RequireKey(DryRunLog log, EventEntity entity, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var states = entity == EventEntity.Node ? log.Snapshot.Nodes : log.Snapshot.Tasks;
        if (!states.ContainsKey(key))
            throw new RunAssertionException($"Unknown {entity} key '{key}'. Available keys: [{string.Join(", ", states.Keys.Order(StringComparer.Ordinal))}].");
    }

    private static void Keys(string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0 || keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new ArgumentException("Specify at least one nonblank, unique key.", nameof(keys));
    }

    private static void Stage(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!LifecycleStages.IsStage(key))
            throw new RunAssertionException($"Unknown lifecycle stage '{key}'; expected $init, $pre, $post or $sys-post.");
    }

    private static string Json(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? "Undefined" : $"{value.ValueKind} {value.GetRawText()}";

    private static void Equal<T>(string subject, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new RunAssertionException($"{subject}: expected {expected}, actual {actual}.");
    }

    private static void Sequence<T>(string subject, IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new RunAssertionException($"{subject}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
    }
}
