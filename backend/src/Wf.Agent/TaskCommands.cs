using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Agent.Logs;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Reports;
using Wf.Contracts.Definition;
using Wf.Contracts.Host.V1;

namespace Wf.Agent;

public sealed class TaskCommands(AgentOptions options, PackageCache cache, TaskHostPool hosts,
    AgentOutbox outbox, TimeProvider timeProvider, ILogger<TaskCommands> logger, AgentLogService? logs = null) : IAgentCommandHandler, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<AttemptKey, Execution> active = new();
    private readonly HashSet<AttemptKey> accepted = [];
    private readonly SemaphoreSlim fenceSerial = new(1, 1);
    private bool disposed;
    private bool recovered;
    private bool fencePending;
    private Drain? drain;
    private Task drainWork = Task.CompletedTask;
    private ulong drainedEpoch;
    private readonly CancellationTokenSource drainStop = new();
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<AttemptRef> Inflight => active.Values.Select(item => item.Dispatch.Task.Clone()).ToArray();
    public Task Failure => failure.Task;

    public async Task RecoverAsync(CancellationToken stoppingToken)
    {
        lock (gate)
        {
            if (recovered) return;
            recovered = true;
        }
        var attempts = await hosts.RecoverAsync(cache.RestoreExistingAsync, stoppingToken);
        var recoveryEpoch = attempts.Aggregate(outbox.ReservationEpoch,
            (epoch, attempt) => Math.Max(epoch, attempt.Dispatch.ReservationEpoch));
        outbox.ObserveReservationEpoch(recoveryEpoch);
        foreach (var attempt in attempts.Where(attempt => attempt.Dispatch.ReservationEpoch < outbox.ReservationEpoch))
        {
            await hosts.DiscardRecoveredAsync(attempt.Dispatch.Task);
            attempt.Acknowledge?.Invoke();
            logger.LogWarning("Discarded stale recovered task host at machine epoch {Epoch}.", attempt.Dispatch.ReservationEpoch);
        }
        if (logs is not null)
            await logs.RecoverAsync(stoppingToken, attempts.Where(attempt => (attempt.Error is null || attempt.SpoolDirectory is not null) &&
                attempt.Dispatch.ReservationEpoch >= outbox.ReservationEpoch).Select(attempt => attempt.Dispatch.Task));
        foreach (var attempt in attempts.Where(attempt => attempt.SpoolDirectory is not null &&
                     attempt.Dispatch.ReservationEpoch >= outbox.ReservationEpoch))
        {
            AgentLogService.Capture? capture = logs is null ? null : await logs.BeginAsync(attempt.Dispatch, resume: true);
            ulong previous = 0;
            foreach (var path in Directory.EnumerateFiles(attempt.SpoolDirectory!, "*.record").Order(StringComparer.Ordinal))
            {
                stoppingToken.ThrowIfCancellationRequested();
                LogSpooler.RejectLinks(path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length is <= 0 or > 128 * 1024)
                    throw new InvalidDataException("Recovered host record exceeds its protocol limit.");
                var record = AttemptRecord.Parser.ParseFrom(file);
                if (record.Sequence <= previous || !record.Task.Equals(attempt.Dispatch.Task) ||
                    record.ReservationEpoch != attempt.Dispatch.ReservationEpoch ||
                    Path.GetFileName(path) != record.Sequence.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".record")
                    throw new InvalidDataException("Recovered host record identity or cursor is invalid.");
                if (record.Log is { At: not null } log && log.Stream is "stdout" or "stderr" && log.Text.Length <= 64 * 1024)
                {
                    if (capture is not null)
                    {
                        if (!await capture.WriteRecordAsync(record))
                            throw new IOException("Recovered host logs could not be durably committed; source spool retained.");
                    }
                    else throw new InvalidOperationException("Recovered host logs require a durable agent log service; attempt spool retained.");
                }
                else if (record.Resource is { } sample && sample.Task.Equals(attempt.Dispatch.Task) &&
                    sample.ReservationEpoch == attempt.Dispatch.ReservationEpoch)
                {
                    _ = Wf.Contracts.Resources.ResourceContract.Parse(sample.SampleJson.Memory);
                    outbox.Enqueue(new AgentMessage { ResourceSample = sample });
                }
                else if (record.Progress is { } update && update.Task.Equals(attempt.Dispatch.Task) &&
                    update.ReservationEpoch == attempt.Dispatch.ReservationEpoch)
                {
                    _ = Wf.Contracts.Progress.TaskProgressProtocol.Validate(update);
                    outbox.PublishProgress(update);
                }
                else throw new InvalidDataException("Invalid recovered host record payload.");
                previous = record.Sequence;
            }
            if (capture is not null) await capture.CompleteAsync(captureInterrupted: attempt.Completed is null);
        }
        lock (gate)
        {
            var executions = new List<(AttemptKey Key, Execution Execution)>();
            foreach (var attempt in attempts.Where(attempt => attempt.Dispatch.ReservationEpoch >= outbox.ReservationEpoch))
            {
                var dispatch = attempt.Dispatch;
                var key = Key(dispatch.Task);
                accepted.Add(key);
                if (attempt.ResourceSample is { } sample) outbox.Enqueue(new AgentMessage { ResourceSample = sample });
                if (attempt.Completed is { } completed)
                {
                    if (!outbox.ContainsResult(dispatch.Task))
                        outbox.Enqueue(new AgentMessage { TaskCompleted = completed });
                    attempt.Acknowledge?.Invoke();
                    continue;
                }
                if (attempt.Error is { } error)
                {
                    if (!outbox.ContainsResult(dispatch.Task))
                        outbox.Enqueue(new AgentMessage { TaskCompleted = new()
                        {
                            Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch,
                            At = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()), Outcome = TaskOutcome.Failed,
                            Error = new() { Category = "AgentLost", Message = error, NonRetryable = false }
                        } });
                    attempt.Acknowledge?.Invoke();
                    continue;
                }
                var execution = new Execution(dispatch.Clone(),
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)) { Recovered = true };
                if (!active.TryAdd(key, execution))
                    throw new InvalidDataException("Recovered attempt is already active.");
                executions.Add((key, execution));
            }
            foreach (var (key, execution) in executions)
                execution.Work = Task.Run(async () =>
                {
                    try { await ExecuteAsync(key, execution); }
                    catch (Exception error) { failure.TrySetException(error); throw; }
                }, CancellationToken.None);
        }
    }

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
            case ControlMessage.PayloadOneofCase.Drain:
                BeginDrain(message.Drain, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"Agent command '{message.PayloadCase}' is not implemented.");
        }
    }

    private void BeginDrain(Drain command, CancellationToken stoppingToken)
    {
        _ = Wf.Contracts.DrainProtocol.Validate(command);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (command.ReservationEpoch < outbox.ReservationEpoch)
            {
                logger.LogWarning("Rejected stale drain {Id} at epoch {Epoch}.", command.Id, command.ReservationEpoch);
                return;
            }
            if (drain is not null && (drain.Id != command.Id || !drain.DeadlineAt.Equals(command.DeadlineAt)))
                throw new InvalidDataException("An active drain cannot change identity or deadline.");
            outbox.ObserveReservationEpoch(command.ReservationEpoch);
            drain ??= command.Clone();
            if (!drainWork.IsCompleted || drainedEpoch >= command.ReservationEpoch) return;
            var executions = active.Values.ToArray();
            drainWork = Task.Run(async () =>
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, drainStop.Token);
                try
                {
                    var work = Task.WhenAll(executions.Select(execution => execution.Work));
                    using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    var remaining = command.DeadlineAt.ToDateTimeOffset() - timeProvider.GetUtcNow();
                    var deadline = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, timerStop.Token);
                    if (await Task.WhenAny(work, deadline) != work)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        lock (gate)
                            foreach (var execution in executions)
                                if (active.Values.Contains(execution))
                                {
                                    execution.DrainInterrupted = true;
                                    execution.Cancellation.Cancel();
                                }
                    }
                    await timerStop.CancelAsync();
                    await work.WaitAsync(stop.Token);
                    await fenceSerial.WaitAsync(stop.Token);
                    try
                    {
                        await hosts.FenceAsync(stop.Token);
                        lock (gate)
                        {
                            var epoch = outbox.ReservationEpoch;
                            outbox.Enqueue(new AgentMessage { DrainCompleted = new() { Id = command.Id, ReservationEpoch = epoch } });
                            drainedEpoch = epoch;
                        }
                    }
                    finally { fenceSerial.Release(); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error)
                {
                    failure.TrySetException(error);
                    throw;
                }
            }, CancellationToken.None);
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
        if (dispatch.Limits is { } limits) _ = new TaskResourceLimits(limits.CpuCores, limits.RamMb);
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
            if (drain is not null)
            {
                logger.LogWarning("Rejected new task {RunId}/{NodeKey}/{TaskKey}/{Attempt}: machine is draining.",
                    key.RunId, key.NodeKey, key.TaskKey, key.Attempt);
                return;
            }
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
        var persisted = execution.Recovered && outbox.ContainsResult(dispatch.Task);
        var publishing = false;
        AgentLogService.Capture? capture = null;
        try
        {
            if (logs is not null)
            {
                try { capture = await logs.BeginAsync(dispatch, resume: execution.Recovered); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                { logs.Report(error); }
            }
            if (!execution.Recovered)
                outbox.Enqueue(new AgentMessage
                {
                    TaskStarted = new TaskStarted { Task = dispatch.Task.Clone(), At = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
                        ReservationEpoch = dispatch.ReservationEpoch }
                });
            using var package = execution.Recovered ? hosts.RecoveredPackage(dispatch.Task) :
                await cache.GetAsync(dispatch.Version, new Uri(dispatch.PackageUri), dispatch.Sha256, execution.Cancellation.Token);
            result = await hosts.ExecuteAsync(package, dispatch, cancellationToken: execution.Cancellation.Token,
                captureLog: capture is null ? null : capture.WriteAsync,
                captureRecord: logs is null ? null : capture is null ? _ => Task.FromResult(false) : capture.WriteRecordAsync,
                publishResources: outbox.Enqueue, publishCheckpoint: outbox.PublishCheckpointAsync,
                publishProgress: outbox.PublishProgress, journalResult: completion =>
                {
                    if (persisted) return;
                    if (execution.DrainInterrupted && completion.Outcome == TaskOutcome.Cancelled) return;
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
        if (!persisted && !(execution.DrainInterrupted && result!.Outcome == TaskOutcome.Cancelled))
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
            drainStop.Cancel();
            executions = active.Values.ToArray();
            foreach (var execution in executions) execution.Cancellation.Cancel();
        }
        await Task.WhenAll(executions.Select(execution => execution.Work));
        await drainWork;
    }

    private sealed record AttemptKey(Guid RunId, string NodeKey, string TaskKey, uint Attempt);
    private sealed class Execution(Dispatch dispatch, CancellationTokenSource cancellation)
    {
        public Dispatch Dispatch { get; } = dispatch;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Work { get; set; } = Task.CompletedTask;
        public volatile bool DrainInterrupted;
        public bool Recovered { get; init; }
    }
}
