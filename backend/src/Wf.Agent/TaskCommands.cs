using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Agent.Logs;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Reports;

namespace Wf.Agent;

public sealed class TaskCommands(AgentOptions options, PackageCache cache, TaskHostPool hosts,
    AgentOutbox outbox, TimeProvider timeProvider, ILogger<TaskCommands> logger, AgentLogService? logs = null) : IAgentCommandHandler, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<AttemptKey, Execution> active = new();
    private readonly HashSet<AttemptKey> accepted = [];
    private readonly SemaphoreSlim fenceSerial = new(1, 1);
    private bool disposed;
    private bool fencePending;
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<AttemptRef> Inflight => active.Values.Select(item => item.Dispatch.Task.Clone()).ToArray();
    public Task Failure => failure.Task;

    public async ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken)
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
            case ControlMessage.PayloadOneofCase.ReservationFence:
                await FenceAsync(message.ReservationFence.ReservationEpoch, cancellationToken);
                break;
            case ControlMessage.PayloadOneofCase.Calibrate:
                Calibrate(message.Calibrate);
                break;
            default:
                throw new NotSupportedException($"Agent command '{message.PayloadCase}' is not implemented.");
        }
    }

    private void Calibrate(Calibrate command)
    {
        _ = Wf.Contracts.CalibrationProtocol.Validate(command);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (outbox.ContainsCalibrationResult(command.Id, command.ReservationEpoch)) return;
            if (command.ReservationEpoch < outbox.ReservationEpoch)
            {
                logger.LogWarning("Rejected stale calibration {Id} at machine epoch {Epoch}.", command.Id, command.ReservationEpoch);
                return;
            }
            var busy = fencePending || !active.IsEmpty;
            if (!busy) outbox.ObserveReservationEpoch(command.ReservationEpoch);
            outbox.Enqueue(new AgentMessage
            {
                CalibrationCompleted = new()
                {
                    Id = command.Id, ReservationEpoch = command.ReservationEpoch,
                    At = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
                    Outcome = busy ? CalibrationOutcome.Busy :
                        timeProvider.GetUtcNow() >= command.DeadlineAt.ToDateTimeOffset()
                            ? CalibrationOutcome.TimedOut : CalibrationOutcome.Unsupported
                }
            });
        }
    }

    private async Task FenceAsync(ulong epoch, CancellationToken cancellationToken)
    {
        if (epoch == 0) throw new InvalidDataException("Reservation fence must be positive.");
        await fenceSerial.WaitAsync(cancellationToken);
        try
        {
            Execution[] cancelled;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (epoch < outbox.ReservationEpoch || active.Values.Any(execution => execution.Dispatch.ReservationEpoch >= epoch))
                {
                    logger.LogWarning("Rejected stale reservation fence {Epoch}; newer machine work must not be stopped.", epoch);
                    return;
                }
                if (epoch > outbox.ReservationEpoch) outbox.ObserveReservationEpoch(epoch);
                fencePending = true;
                cancelled = active.Values.ToArray();
                foreach (var execution in cancelled) execution.Cancellation.Cancel();
            }
            await hosts.FenceAsync(cancellationToken);
            await Task.WhenAll(cancelled.Select(execution => execution.Work)).WaitAsync(cancellationToken);
            lock (gate)
            {
                outbox.Enqueue(new AgentMessage { ReservationFenced = new() { ReservationEpoch = epoch } });
                fencePending = false;
            }
        }
        finally { fenceSerial.Release(); }
    }

    private void Accept(Dispatch dispatch, CancellationToken stoppingToken)
    {
        var key = Key(dispatch.Task);
        if (string.IsNullOrWhiteSpace(dispatch.Version) || dispatch.DeadlineAt is null ||
            dispatch.ReservationEpoch == 0 || !Uri.TryCreate(dispatch.PackageUri, UriKind.Absolute, out _) ||
            dispatch.Sha256.Length != 64 || dispatch.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Dispatch requires a version, deadline, reservation, package URL and SHA-256.");
        _ = dispatch.DeadlineAt.ToDateTimeOffset();
        if (dispatch.Grants.Count(grant => grant.Kind == "reports") > 1)
            throw new InvalidDataException("Dispatch contains duplicate report capabilities.");
        if (dispatch.Grants.Count(grant => grant.Kind == "artifacts") > 1)
            throw new InvalidDataException("Dispatch contains duplicate artifact capabilities.");
        if (dispatch.Grants.Count(grant => grant.Kind == "checkpoints") > 1)
            throw new InvalidDataException("Dispatch contains duplicate checkpoint capabilities.");
        if (dispatch.Checkpoint is not null)
            _ = Wf.Contracts.Checkpoints.CheckpointProtocol.ValidateSeed(dispatch.Checkpoint, dispatch.Task);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (dispatch.ReservationEpoch < outbox.ReservationEpoch)
            {
                logger.LogWarning("Rejected stale reservation epoch {Epoch} for task {RunId}/{NodeKey}/{TaskKey}/{Attempt}.",
                    dispatch.ReservationEpoch, key.RunId, key.NodeKey, key.TaskKey, key.Attempt);
                return;
            }
            if (fencePending)
                throw new InvalidDataException("Dispatch cannot start until physical reservation fencing completes.");
            if (accepted.Contains(key) || outbox.ContainsResult(dispatch.Task)) return;
            if (active.Count >= options.MaxTasks)
                throw new InvalidDataException("Dispatch exceeds the agent's configured task capacity.");
            var execution = new Execution(dispatch.Clone(), CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
            foreach (var grant in execution.Dispatch.Grants.Where(grant => grant.Kind is "reports" or "artifacts" or "checkpoints"))
            {
                if (!Uri.TryCreate(options.Engines[0], grant.UploadUri, out var uri) ||
                    !options.Engines.Any(engine => engine.GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority)) ||
                    uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                    uri.AbsolutePath != (grant.Kind switch
                    {
                        "reports" => ReportUploadMetadata.EndpointPath,
                        "artifacts" => Wf.Contracts.Artifacts.ArtifactMetadata.GrantPath,
                        _ => Wf.Contracts.Checkpoints.CheckpointProtocol.EndpointPath
                    }))
                {
                    execution.Cancellation.Dispose();
                    throw new InvalidDataException("Upload capability must target its configured engine endpoint.");
                }
                grant.UploadUri = uri.AbsoluteUri;
            }
            if (active.Values.Any(item => item.Dispatch.ReservationEpoch < dispatch.ReservationEpoch))
            {
                execution.Cancellation.Dispose();
                throw new InvalidDataException("A newer reservation cannot start before older machine work is fenced.");
            }
            outbox.ObserveReservationEpoch(dispatch.ReservationEpoch);
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
        AgentLogService.Capture? capture = null;
        try
        {
            if (logs is not null)
            {
                try { capture = await logs.BeginAsync(dispatch); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                { logs.Report(error); }
            }
            outbox.Enqueue(new AgentMessage
            {
                TaskStarted = new TaskStarted { Task = dispatch.Task.Clone(), At = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
                    ReservationEpoch = dispatch.ReservationEpoch }
            });
            using var package = await cache.GetAsync(dispatch.Version, new Uri(dispatch.PackageUri), dispatch.Sha256,
                execution.Cancellation.Token);
            result = await hosts.ExecuteAsync(package, dispatch, cancellationToken: execution.Cancellation.Token,
                captureLog: capture is null ? null : capture.WriteAsync,
                publishResources: outbox.Enqueue, publishCheckpoint: outbox.PublishCheckpointAsync,
                publishProgress: outbox.PublishProgress, journalResult: completion =>
                {
                    publishing = true;
                    completion.ReservationEpoch = dispatch.ReservationEpoch;
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
        if (capture is not null) await capture.CompleteAsync();
        if (!persisted)
        {
            result!.ReservationEpoch = dispatch.ReservationEpoch;
            outbox.Enqueue(new AgentMessage { TaskCompleted = result });
        }
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
