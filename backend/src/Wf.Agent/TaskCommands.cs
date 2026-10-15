using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent;

public sealed class TaskCommands(AgentOptions options, PackageCache cache, TaskHostPool hosts,
    AgentOutbox outbox, TimeProvider timeProvider, ILogger<TaskCommands> logger) : IAgentCommandHandler, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<AttemptKey, Execution> active = new();
    private readonly HashSet<AttemptKey> accepted = [];
    private bool disposed;
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<AttemptRef> Inflight => active.Values.Select(item => item.Dispatch.Task.Clone()).ToArray();
    public Task Failure => failure.Task;

    public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        switch (message.PayloadCase)
        {
            case ControlMessage.PayloadOneofCase.Dispatch:
                Accept(message.Dispatch, cancellationToken);
                break;
            case ControlMessage.PayloadOneofCase.Cancel:
                var key = Key(message.Cancel.Task);
                lock (gate)
                    if (active.TryGetValue(key, out var execution)) execution.Cancellation.Cancel();
                break;
            default:
                throw new NotSupportedException($"Agent command '{message.PayloadCase}' is not implemented.");
        }
        return ValueTask.CompletedTask;
    }

    private void Accept(Dispatch dispatch, CancellationToken stoppingToken)
    {
        var key = Key(dispatch.Task);
        if (string.IsNullOrWhiteSpace(dispatch.Version) || dispatch.DeadlineAt is null ||
            dispatch.ReservationEpoch == 0 || !Uri.TryCreate(dispatch.PackageUri, UriKind.Absolute, out _) ||
            dispatch.Sha256.Length != 64 || dispatch.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Dispatch requires a version, deadline, reservation, package URL and SHA-256.");
        _ = dispatch.DeadlineAt.ToDateTimeOffset();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (accepted.Contains(key) || outbox.ContainsResult(dispatch.Task)) return;
            if (active.Count >= options.MaxTasks)
                throw new InvalidDataException("Dispatch exceeds the agent's configured task capacity.");
            var execution = new Execution(dispatch.Clone(), CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
            accepted.Add(key);
            active[key] = execution;
            execution.Work = Task.Run(async () =>
            {
                try { await ExecuteAsync(key, execution); }
                catch (Exception exception)
                {
                    failure.TrySetException(exception);
                    throw;
                }
            }, CancellationToken.None);
        }
    }

    private async Task ExecuteAsync(AttemptKey key, Execution execution)
    {
        var dispatch = execution.Dispatch;
        TaskCompleted? result = null;
        var persisted = false;
        var publishing = false;
        try
        {
            outbox.Enqueue(new AgentMessage
            {
                TaskStarted = new TaskStarted { Task = dispatch.Task.Clone(), At = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()) }
            });
            var package = await cache.GetAsync(dispatch.Version, new Uri(dispatch.PackageUri), dispatch.Sha256,
                execution.Cancellation.Token);
            result = await hosts.ExecuteAsync(package, dispatch, cancellationToken: execution.Cancellation.Token,
                journalResult: completion =>
                {
                    publishing = true;
                    outbox.Enqueue(new AgentMessage { TaskCompleted = completion });
                    persisted = true;
                    publishing = false;
                });
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested)
        {
            result = Failure(TaskOutcome.Cancelled, "Aborted", "Attempt cancelled before its task host completed.", true);
        }
        catch (Exception exception) when (!publishing)
        {
            logger.LogError(exception, "Task {RunId}/{NodeKey}/{TaskKey}/{Attempt} failed at the agent boundary.",
                key.RunId, key.NodeKey, key.TaskKey, key.Attempt);
            result = Failure(TaskOutcome.Failed, "PlatformError", exception.Message, false);
        }
        if (!persisted) outbox.Enqueue(new AgentMessage { TaskCompleted = result });
        lock (gate)
        {
            active.TryRemove(key, out _);
            execution.Cancellation.Dispose();
        }

        TaskCompleted Failure(TaskOutcome outcome, string category, string error, bool nonRetryable) => new()
        {
            Task = dispatch.Task.Clone(), At = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()), Outcome = outcome,
            Error = new TaskError { Category = category, Message = error[..Math.Min(error.Length, 1024)], NonRetryable = nonRetryable }
        };
    }

    private static AttemptKey Key(AttemptRef? task)
    {
        if (task is null || !Guid.TryParse(task.RunId, out var runId) || runId == Guid.Empty ||
            string.IsNullOrWhiteSpace(task.NodeKey) || task.NodeKey.Contains('/') ||
            string.IsNullOrWhiteSpace(task.TaskKey) || task.TaskKey.Contains('/') ||
            Encoding.UTF8.GetByteCount(task.NodeKey) > 512 || Encoding.UTF8.GetByteCount(task.TaskKey) > 512 ||
            task.Attempt is 0 or > 32767)
            throw new InvalidDataException("Command requires a valid run, node, task and positive attempt.");
        return new AttemptKey(runId, task.NodeKey, task.TaskKey, task.Attempt);
    }

    public async ValueTask DisposeAsync()
    {
        Execution[] executions;
        lock (gate)
        {
            disposed = true;
            executions = active.Values.ToArray();
            foreach (var execution in executions) execution.Cancellation.Cancel();
        }
        await Task.WhenAll(executions.Select(execution => execution.Work));
    }

    private sealed record AttemptKey(Guid RunId, string NodeKey, string TaskKey, uint Attempt);
    private sealed class Execution(Dispatch dispatch, CancellationTokenSource cancellation)
    {
        public Dispatch Dispatch { get; } = dispatch;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Work { get; set; } = Task.CompletedTask;
    }
}
