using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core;
using Wf.Engine.Core.Abort;
using Wf.Engine.Core.Metrics;
using Wf.Engine.Core.Retries;
using Wf.Engine.Core.Scheduling;
using Wf.Engine.Core.Stages;
using Wf.Engine.Core.StateMachine;
using Wf.Engine.Core.Timers;
using Wf.Engine.Core.Transitions;
using Wf.Validation;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Local;

public sealed record LocalTaskAttempt(string TaskKey, int Attempt, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    ExecutionStatus Status, FailureCategory? Failure, Exception? Error);
public sealed record LocalRunResult(RunSnapshot Snapshot, ImmutableArray<RunEvent> Events,
    ImmutableArray<LocalTaskAttempt> Attempts, ImmutableDictionary<string, ImmutableDictionary<string, JsonElement>> Outputs)
{
    public int ExitCode => Snapshot.Status == RunStatus.Succeeded ? 0 : 1;
}

public sealed class LocalEngine
{
    private enum CancellationCause { None, Abort, HardFailure, Timeout }
    private abstract record Message;
    private sealed record AbortMessage(AbortRecord Record, TaskCompletionSource<AbortRecord>? Reply = null) : Message;
    private sealed record Completed(string Key, Exception? Error) : Message;
    private sealed class Attempt(CancellationTokenSource cancellation, DateTimeOffset startedAt, int number)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int Number { get; } = number;
        public CancellationCause Cause { get; set; }
        public Task CancellationCompleted { get; set; } = Task.CompletedTask;
    }
    private sealed class Clock(TimeProvider time) : IClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    private readonly WorkflowDefinition workflow;
    private readonly RunState run;
    private readonly TaskRegistry registry;
    private readonly ConsoleRunRenderer renderer;
    private readonly TimeProvider time;
    private readonly TimeSpan grace;
    private readonly IReports? reports;
    private readonly IArtifacts? artifacts;
    private readonly ImmutableDictionary<string, object> variables;
    private readonly Dictionary<string, NodeOutputs> outputs;
    private readonly Dictionary<string, Attempt> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Author, int Platform, int Attempts)> budgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> nodeDeadlines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionStatus> stoppedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> retryReady = new(StringComparer.Ordinal);
    private readonly List<LocalTaskAttempt> attempts = [];
    private readonly Channel<Message> messages = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly DeadlineTracker timers;
    private DateTimeOffset runDeadline;
    private bool timedOut;
    private int started;
    public RunSnapshot Snapshot => run.Snapshot;

    public LocalEngine(WorkflowDefinition workflow, IReadOnlyDictionary<string, JsonElement>? variables = null,
        TextWriter? output = null, TimeProvider? timeProvider = null, IReports? reports = null, IArtifacts? artifacts = null,
        TimeSpan? cancellationGrace = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Nodes.SelectMany(node => node.Tasks).Any(task =>
            task.Policy.Limits is { CpuCores: > 0 } or { RamMb: > 0 }))
            throw new NotSupportedException("Per-task CPU/RAM limits require agent execution; in-process local execution cannot enforce them.");
        this.reports = reports is ReportPublisher publisher ? publisher.ForRun() : reports;
        this.artifacts = artifacts;
        renderer = new(output ?? Console.Out);
        time = timeProvider ?? TimeProvider.System;
        grace = cancellationGrace ?? TimeSpan.FromSeconds(30);
        if (grace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancellationGrace));
        var diagnostics = PolicyRules.Validate(workflow.Nodes, workflow.RunTimeout);
        var errors = diagnostics.Where(d => d.Severity == PolicyDiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors.Select(d => d.Message)), nameof(workflow));
        foreach (var warning in diagnostics)
            renderer.Log($"Warning: {warning.Message}");
        this.variables = VariableBinder.Bind(workflow.Variables, variables ?? ImmutableDictionary<string, JsonElement>.Empty);
        this.workflow = new(workflow.Name, workflow.Version, PlacementResolver.Resolve(workflow.Nodes, this.variables),
            workflow.Tasks, workflow.Variables, workflow.Metadata, workflow.RunTimeout, workflow.Placement);
        run = new(Guid.CreateVersion7(), this.workflow.Nodes, workflow.Metadata, renderer.Log, time.GetUtcNow(),
            this.variables.ToDictionary(p => p.Key,
                p => JsonSerializer.SerializeToElement(p.Value, OutputJsonContext.Default.Object), StringComparer.Ordinal));
        registry = new(workflow);
        outputs = workflow.Nodes.ToDictionary(n => n.Key, n => new NodeOutputs(n.Key), StringComparer.Ordinal);
        timers = new(new Clock(time), grace);
    }

    public Task<AbortRecord> AbortAsync(AbortRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.At > Now)
            throw new ArgumentException("An accepted abort record cannot be dated in the future.", nameof(record));
        var reply = new TaskCompletionSource<AbortRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!messages.Writer.TryWrite(new AbortMessage(record, reply)))
            ReplyToClosedAbort(reply);
        return reply.Task;
    }

    public async Task<LocalRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("A local engine instance can execute only one run.");
        using var registration = cancellationToken.Register(() => messages.Writer.TryWrite(new AbortMessage(
            new("local", "Local user", Now, "Cancellation requested", AbortSource.Local))));
        try
        {
            DrainMessages();
            if (run.Snapshot.Status == RunStatus.Queued)
            {
                Emit(EventEntity.Run, null, (int)RunStatus.Running);
                runDeadline = Add(Now, workflow.RunTimeout);
                timers.Track(new(new(TimerScope.Run, "run"), DeadlineAt: runDeadline));
            }
            while (!TransitionTable.IsTerminal(run.Snapshot.Status))
            {
                DrainMessages();
                ProcessTimers();
                long previous;
                do
                {
                    previous = run.Snapshot.LastSequence;
                    Reconcile();
                } while (!TransitionTable.IsTerminal(run.Snapshot.Status) && run.Snapshot.LastSequence != previous);
                if (!TransitionTable.IsTerminal(run.Snapshot.Status))
                    await WaitForWorkAsync().ConfigureAwait(false);
            }
            WorkflowTelemetry.RecordCompletion(new("local", workflow.Metadata.Type, workflow.Name), run.Snapshot);
            return new(run.Snapshot, run.Events, attempts.ToImmutableArray(), outputs.ToImmutableDictionary(
                p => p.Key, p => p.Value.Values, StringComparer.Ordinal));
        }
        finally
        {
            foreach (var key in active.Keys)
                CancelAttempt(key, CancellationCause.Abort);
            messages.Writer.TryComplete();
            while (messages.Reader.TryRead(out var message))
                if (message is AbortMessage { Reply: { } reply })
                    ReplyToClosedAbort(reply);
                else if (message is Completed completed && active.Remove(completed.Key, out var attempt))
                    _ = DisposeCancellationAsync(attempt);
        }
    }

    private DateTimeOffset Now => time.GetUtcNow();
    private void Emit(EventEntity entity, string? key, int state, AbortRecord? abort = null,
        ExecutionStatus? attemptStatus = null, DateTimeOffset? attemptEndedAt = null, FailureCategory? failureCategory = null)
    {
        var committed = run.CreateEvent(entity, key, state, Now, abort) with
            { AttemptStatus = attemptStatus, AttemptEndedAt = attemptEndedAt, FailureCategory = failureCategory };
        run.Apply(committed);
        renderer.Render(committed);
    }

    private void DrainMessages()
    {
        while (messages.Reader.TryRead(out var message))
            switch (message)
            {
                case AbortMessage abort:
                    if (TransitionTable.IsTerminal(run.Snapshot.Status) && run.Snapshot.Abort is null)
                        abort.Reply?.TrySetException(new InvalidOperationException($"Run is already {run.Snapshot.Status}."));
                    else
                    {
                        ApplyAbort(AbortHandler.Request(run.Snapshot, abort.Record));
                        RemoveTimer(new(TimerScope.Run, "run"));
                        abort.Reply?.TrySetResult(run.Snapshot.Abort ??
                            throw new InvalidOperationException("An accepted abort must preserve its original record."));
                    }
                    break;
                case Completed completed:
                    CompleteAttempt(completed);
                    break;
            }
    }

    private void Reconcile()
    {
        if (TransitionTable.IsTerminal(run.Snapshot.Status))
            return;
        if (run.Snapshot.Status == RunStatus.Aborting)
            ApplyAbort(AbortHandler.Plan(workflow.Nodes, run.Snapshot));
        if (TransitionTable.IsTerminal(run.Snapshot.Status))
            return;
        foreach (var node in workflow.Nodes.Where(n => run.Snapshot.Nodes[n.Key] == ExecutionStatus.Running))
        {
            var local = node.Tasks.ToDictionary(t => t.Key, t => run.Snapshot.Tasks[$"{node.Key}/{t.Key}"]);
            if (FailurePropagation.ResolveNode(node, local) is { } outcome)
            {
                Emit(EventEntity.Node, node.Key, (int)stoppedNodes.GetValueOrDefault(node.Key, outcome),
                    failureCategory: stoppedNodes.ContainsKey(node.Key) ? FailureCategory.Timeout : null);
                RemoveTimer(new(TimerScope.Node, node.Key));
            }
        }
        var stage = StagePlanner.Plan(workflow.Nodes, run.Snapshot.Nodes, run.Snapshot.Status, timedOut);
        foreach (var change in stage.Changes)
            ChangeNode(change);
        foreach (var change in ReadinessEvaluator.Evaluate(workflow.Nodes, run.Snapshot.Nodes, stage.EligibleNodes))
            ChangeNode(change);
        if (stage.Complete)
        {
            if (active.Count != 0 || run.Snapshot.Tasks.Values.Any(s => !TransitionTable.IsTerminal(s)))
                throw new InvalidOperationException("The local run cannot finish before every task has drained.");
            if (run.Snapshot.Status == RunStatus.Aborting)
                ApplyAbort(AbortHandler.Plan(workflow.Nodes, run.Snapshot));
            else
                Emit(EventEntity.Run, null, (int)(timedOut ? RunStatus.TimedOut :
                    run.Snapshot.Nodes.Values.Any(s => s is ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Cancelled)
                        ? RunStatus.Failed : RunStatus.Succeeded),
                    failureCategory: timedOut ? FailureCategory.Timeout : null);
            return;
        }
        foreach (var node in workflow.Nodes)
        {
            if (run.Snapshot.Nodes[node.Key] == ExecutionStatus.Ready)
            {
                Emit(EventEntity.Node, node.Key, (int)ExecutionStatus.Dispatched);
                Emit(EventEntity.Node, node.Key, (int)ExecutionStatus.Running);
                if (node.Timeout is { } timeout)
                {
                    var deadline = Add(Now, timeout);
                    nodeDeadlines.Add(node.Key, deadline);
                    timers.Track(new(new(TimerScope.Node, node.Key), DeadlineAt: deadline));
                }
            }
            if (run.Snapshot.Nodes[node.Key] != ExecutionStatus.Running || stoppedNodes.ContainsKey(node.Key))
                continue;
            var limit = node.Mode == NodeExecutionMode.Series ? 1 : node.MaxParallel ?? int.MaxValue;
            var running = node.Tasks.Count(t => active.ContainsKey($"{node.Key}/{t.Key}"));
            foreach (var task in node.Tasks)
            {
                if (running >= limit)
                    break;
                var key = $"{node.Key}/{task.Key}";
                var state = run.Snapshot.Tasks[key];
                if (state == ExecutionStatus.Pending || (state == ExecutionStatus.Retrying && retryReady.Remove(key)))
                {
                    StartAttempt(node, task);
                    running++;
                }
                if (node.Mode == NodeExecutionMode.Series && !TransitionTable.IsTerminal(run.Snapshot.Tasks[key]))
                    break;
            }
        }
    }

    private void StartAttempt(NodeDefinition node, TaskDefinition task)
    {
        var key = $"{node.Key}/{task.Key}";
        if (run.Snapshot.Tasks[key] == ExecutionStatus.Pending)
            Emit(EventEntity.Task, key, (int)ExecutionStatus.Ready);
        Emit(EventEntity.Task, key, (int)ExecutionStatus.Dispatched);
        Emit(EventEntity.Task, key, (int)ExecutionStatus.Running);
        var budget = budgets.GetValueOrDefault(key);
        budgets[key] = (budget.Author, budget.Platform, budget.Attempts + 1);
        var attempt = new Attempt(new(), Now, budget.Attempts + 1);
        active.Add(key, attempt);
        timers.Track(new(new(TimerScope.Attempt, key), DeadlineAt: Add(attempt.StartedAt, task.Policy.AttemptTimeout)));
        var context = new TaskContext(attempt.Cancellation.Token, variables, outputs[node.Key], outputs,
            node.Inputs, message => renderer.Log($"{key}: {message}"), reports, artifacts, abort: run.Snapshot.Abort,
            attempt: new(checked((uint)attempt.Number)), runId: run.Snapshot.RunId);
        if (time is DryRunClock)
            _ = ExecuteAsync();
        else
            _ = Task.Run(ExecuteAsync, CancellationToken.None);
        async Task ExecuteAsync()
        {
            Exception? error = null;
            try { await registry.ExecuteAsync(key, context).ConfigureAwait(false); }
            catch (Exception failure) { error = failure; }
            if (attempt.Cancellation.IsCancellationRequested)
                await attempt.CancellationCompleted.ConfigureAwait(false);
            if (!messages.Writer.TryWrite(new Completed(key, error)))
                await DisposeCancellationAsync(attempt).ConfigureAwait(false);
        }
    }

    private void CompleteAttempt(Completed completed)
    {
        if (!active.Remove(completed.Key, out var attempt))
            throw new InvalidOperationException($"Task '{completed.Key}' has no active attempt to complete.");
        RemoveTimer(new(TimerScope.Attempt, completed.Key));
        _ = DisposeCancellationAsync(attempt);
        var node = workflow.Nodes.Single(n => n.Key == completed.Key[..completed.Key.IndexOf('/')]);
        var task = node.Tasks.Single(t => $"{node.Key}/{t.Key}" == completed.Key);
        var cancelled = attempt.Cause is CancellationCause.Abort or CancellationCause.HardFailure;
        var failed = completed.Error is not null || attempt.Cause != CancellationCause.None;
        var category = failed ? FailureClassifier.Classify(completed.Error, FailureCategory.TaskError,
            timedOut: attempt.Cause == CancellationCause.Timeout, aborted: cancelled) : (FailureCategory?)null;
        // Preserve the lifecycle timeout independently of an error reported while cancellation drains.
        var attemptTimedOut = attempt.Cause == CancellationCause.Timeout ||
            completed.Error is TaskFailureException { Category: FailureCategory.Timeout };
        var status = cancelled ? ExecutionStatus.Cancelled : !failed ? ExecutionStatus.Succeeded :
            task.Failure == FailurePolicy.Soft ? ExecutionStatus.SoftFailed :
            attemptTimedOut ? ExecutionStatus.TimedOut : ExecutionStatus.Failed;
        attempts.Add(new(completed.Key, attempt.Number, attempt.StartedAt, Now, status,
            category, completed.Error));
        if (completed.Error is { } error)
            renderer.Log($"{completed.Key}: {error.GetType().Name}: {error.Message}");
        if (!failed || cancelled)
        {
            Emit(EventEntity.Task, completed.Key, (int)status, failureCategory: category);
            return;
        }
        var budget = budgets[completed.Key];
        var cleanup = LifecycleStages.After.Contains(node.Key) ||
            node.Dependencies.Any(d => d.When == DependencyCondition.Always);
        var deadline = cleanup && (timedOut || run.Snapshot.Status == RunStatus.Aborting)
            ? DateTimeOffset.MaxValue : runDeadline;
        var retry = RetryPolicyEvaluator.Evaluate(task.Policy, new(category!.Value, budget.Author, budget.Platform, Now, deadline,
            nodeDeadlines.GetValueOrDefault(node.Key, DateTimeOffset.MaxValue),
            Cancelled: stoppedNodes.ContainsKey(node.Key)), time is DryRunClock ? 0.5 : Random.Shared.NextDouble(), completed.Error);
        if (retry.Retry)
        {
            budgets[completed.Key] = (budget.Author + (retry.Budget == RetryBudget.Author ? 1 : 0),
                budget.Platform + (retry.Budget == RetryBudget.Platform ? 1 : 0), budget.Attempts);
            Emit(EventEntity.Task, completed.Key, (int)ExecutionStatus.Retrying,
                attemptStatus: status, attemptEndedAt: attempts[^1].EndedAt, failureCategory: category);
            timers.Track(new(new(TimerScope.Retry, completed.Key), RetryAt: retry.RetryAt));
            return;
        }
        var propagation = FailurePropagation.Plan(node, task.Key,
            node.Tasks.ToDictionary(t => t.Key, t => run.Snapshot.Tasks[$"{node.Key}/{t.Key}"]),
            attemptTimedOut);
        foreach (var change in propagation.Changes)
        {
            var key = $"{node.Key}/{change.Task}";
            RemoveTimer(new(TimerScope.Retry, key));
            retryReady.Remove(key);
            Emit(EventEntity.Task, key, (int)change.Status,
                failureCategory: key == completed.Key ? category : null);
        }
        foreach (var key in propagation.CancelRunning)
            CancelAttempt($"{node.Key}/{key}", CancellationCause.HardFailure);
    }

    private void ProcessTimers()
    {
        while (timers.GetDue().FirstOrDefault() is { } action)
        {
            timers.Apply(action);
            var key = action.Before.Key;
            if (action.Kind == TimerActionKind.Kill)
                throw new InvalidOperationException($"Task '{key.Key}' ignored cancellation for {grace}. " +
                    "An in-process local function cannot be forcibly killed; use an isolated task host.");
            if (action.Kind == TimerActionKind.DispatchRetry)
            {
                timers.Complete(key);
                retryReady.Add(key.Key);
            }
            else if (key.Scope == TimerScope.Attempt)
                CancelAttempt(key.Key, CancellationCause.Timeout);
            else if (key.Scope == TimerScope.Run)
            {
                timers.Complete(key);
                timedOut = true;
                foreach (var node in workflow.Nodes.Where(n => run.Snapshot.Nodes[n.Key] == ExecutionStatus.Running))
                    StopNode(node);
            }
            else if (key.Scope == TimerScope.Node)
            {
                timers.Complete(key);
                StopNode(workflow.Nodes.Single(n => n.Key == key.Key));
            }
        }
    }

    private void StopNode(NodeDefinition node)
    {
        stoppedNodes[node.Key] = node.Failure == FailurePolicy.Soft ? ExecutionStatus.SoftFailed : ExecutionStatus.TimedOut;
        foreach (var task in node.Tasks)
        {
            var key = $"{node.Key}/{task.Key}";
            if (active.ContainsKey(key))
                CancelAttempt(key, CancellationCause.Timeout);
            else if (!TransitionTable.IsTerminal(run.Snapshot.Tasks[key]))
            {
                RemoveTimer(new(TimerScope.Retry, key));
                retryReady.Remove(key);
                Emit(EventEntity.Task, key, (int)ExecutionStatus.Skipped);
            }
        }
    }

    private void ApplyAbort(AbortPlan plan)
    {
        foreach (var task in plan.Tasks)
        {
            RemoveTimer(new(TimerScope.Retry, task.Task));
            retryReady.Remove(task.Task);
            Emit(EventEntity.Task, task.Task, (int)task.Status);
        }
        foreach (var node in plan.Nodes)
        {
            ChangeNode(node, node.Status == ExecutionStatus.Cancelled ? FailureCategory.Aborted : null);
            RemoveTimer(new(TimerScope.Node, node.Node));
        }
        if (plan.Status is { } status)
            Emit(EventEntity.Run, null, (int)status, plan.Record);
        foreach (var key in plan.CancelRunning)
            CancelAttempt(key, CancellationCause.Abort);
    }

    private void ChangeNode(NodeStateChange change, FailureCategory? failureCategory = null)
    {
        if (change.Status == ExecutionStatus.Skipped)
            foreach (var task in workflow.Nodes.Single(n => n.Key == change.Node).Tasks)
            {
                var key = $"{change.Node}/{task.Key}";
                if (!TransitionTable.IsTerminal(run.Snapshot.Tasks[key]))
                {
                    RemoveTimer(new(TimerScope.Retry, key));
                    retryReady.Remove(key);
                    Emit(EventEntity.Task, key, (int)ExecutionStatus.Skipped);
                }
            }
        Emit(EventEntity.Node, change.Node, (int)change.Status, failureCategory: failureCategory);
    }

    private void CancelAttempt(string key, CancellationCause cause)
    {
        var attempt = active[key];
        if (attempt.Cause != CancellationCause.None)
            return;
        attempt.Cause = cause;
        RemoveTimer(new(TimerScope.Attempt, key));
        timers.Track(new(new(TimerScope.Attempt, key), DeadlineAt: Now, KillAt: Add(Now, grace),
            Phase: TimerPhase.CancelRequested));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        attempt.CancellationCompleted = completion.Task;
        _ = NotifyCancellationAsync(key, attempt.Cancellation, completion);
    }

    private async Task NotifyCancellationAsync(string key, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException error) { renderer.Log($"{key}: Cancellation callbacks failed: {error}"); }
        finally { completion.TrySetResult(); }
    }

    private static async Task DisposeCancellationAsync(Attempt attempt)
    {
        await attempt.CancellationCompleted.ConfigureAwait(false);
        attempt.Cancellation.Dispose();
    }

    private void RemoveTimer(TimerKey key)
    {
        if (timers.Snapshot.Any(t => t.Key == key))
            timers.Complete(key);
    }

    private async Task WaitForWorkAsync()
    {
        var next = timers.Snapshot.Select(t => t.Phase == TimerPhase.CancelRequested ? t.KillAt :
            t.Phase == TimerPhase.Waiting ? t.DeadlineAt ?? t.RetryAt : null).Where(t => t is not null).Min();
        if (active.Count == 0 && next is null)
            throw new InvalidOperationException("The local run has unfinished work but no dispatch, deadline or retry can make progress.");
        if (time is DryRunClock clock)
        {
            if (messages.Reader.TryPeek(out _))
                return;
            if (active.Values.Any(a => a.Cause != CancellationCause.None))
                await messages.Reader.WaitToReadAsync().ConfigureAwait(false);
            else if (next is { } virtualDeadline)
                clock.AdvanceTo(virtualDeadline < Now ? Now : virtualDeadline);
            return;
        }
        using var wake = new CancellationTokenSource();
        var read = messages.Reader.WaitToReadAsync(wake.Token).AsTask();
        var delay = Task.Delay(next is { } at ? TimeSpan.FromMilliseconds(Math.Clamp((at - Now).TotalMilliseconds,
            0, uint.MaxValue - 1)) : Timeout.InfiniteTimeSpan, time, wake.Token);
        await Task.WhenAny(read, delay).ConfigureAwait(false);
        wake.Cancel();
    }

    private void ReplyToClosedAbort(TaskCompletionSource<AbortRecord> reply)
    {
        if (run.Snapshot.Abort is { } abort)
            reply.TrySetResult(abort);
        else
            reply.TrySetException(new InvalidOperationException("This local run is no longer accepting abort requests."));
    }

    private static DateTimeOffset Add(DateTimeOffset at, TimeSpan duration) =>
        duration >= DateTimeOffset.MaxValue - at ? DateTimeOffset.MaxValue : at + duration;
}
