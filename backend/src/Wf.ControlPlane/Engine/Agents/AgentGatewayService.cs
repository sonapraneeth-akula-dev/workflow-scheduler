using Grpc.Core;
using Npgsql;
using System.Threading.Channels;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.ControlPlane.Storage;
using Wf.ControlPlane.Live;
using Wf.Contracts.Logs;

namespace Wf.ControlPlane.Engine.Agents;

public sealed class AgentGatewayService(AgentRegistry registry, ILogger<AgentGatewayService> logger, RunStore? runs = null,
    LeaseManager? leadership = null, AgentLossSweeper? losses = null, LogUploadCapabilities? logUploads = null,
    Wf.ControlPlane.Reports.ReportUploadCapabilities? reportUploads = null,
    Wf.ControlPlane.Artifacts.ArtifactUploadCapabilities? artifactUploads = null, ResourceSampleSink? resources = null,
    ReservationLeaseService? reservations = null, CalibrationService? calibrations = null,
    Wf.ControlPlane.Engine.Checkpoints.CheckpointApplier? checkpoints = null,
    Wf.ControlPlane.Engine.Checkpoints.CheckpointUploadCapabilities? checkpointUploads = null,
    AgentDrainService? drains = null, LiveLogRelay? liveLogs = null, LiveLogSink? logSink = null,
    LeaseOptions? leaseOptions = null)
    : AgentGateway.AgentGatewayBase
{
    public override async Task Connect(IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlMessage> responseStream, ServerCallContext context)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var token = stopping.Token;
        long sequence = 0;
        try
        {
            var lease = leadership?.RequireLease();
            var epoch = checked((ulong)(lease?.Epoch ?? 1));
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (!await requestStream.MoveNext(helloTimeout.Token)) return;
            var first = requestStream.Current;
            if (first.Sequence == 0 || first.Hello is null) throw Invalid("The first agent message must be a sequenced Hello.");
            var session = await registry.RegisterAsync(first.Hello, token);
            CheckLeadership();
            if (losses is not null) await losses.ReconcileAsync(session, first.Hello.Inflight, token);
            CheckLeadership();
            logger.LogInformation("Agent {AgentId} registered.", session.AgentId);
            var received = first.Sequence;
            var acknowledgements = Channel.CreateBounded<ulong>(32);
            var wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
                { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
            void DispatchCommitted(Guid agentId)
            {
                if (agentId == session.AgentId) wake.Writer.TryWrite(0);
            }
            await acknowledgements.Writer.WriteAsync(received, token);
            registry.DispatchCommitted += DispatchCommitted;
            var sending = SendAsync();
            var receiving = ReceiveAsync();
            try
            {
                await Task.WhenAny(sending, receiving);
                await stopping.CancelAsync();
                await Task.WhenAll(sending, receiving);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
            finally { registry.DispatchCommitted -= DispatchCommitted; }

            async Task ReceiveAsync()
            {
                try
                {
                    await foreach (var message in requestStream.ReadAllAsync(token))
                    {
                        CheckLeadership();
                        if (message.LiveLogs is { } logs)
                        {
                            if (message.Sequence != 0) throw Invalid("Ephemeral log batches must not use the durable sequence.");
                            if (logSink is null) throw new RpcException(new Status(StatusCode.Unimplemented, "Live logs are not configured."));
                            await logSink.AcceptAsync(session, logs, token);
                            continue;
                        }
                        if (message.Sequence == 0) throw Invalid("Agent sequence must be positive.");
                        if (message.Sequence <= received)
                        {
                            if (message.PayloadCase != AgentMessage.PayloadOneofCase.Ack)
                            {
                                await acknowledgements.Writer.WriteAsync(received, token);
                                wake.Writer.TryWrite(0);
                            }
                            continue;
                        }
                        switch (message.PayloadCase)
                        {
                            case AgentMessage.PayloadOneofCase.DrainCompleted:
                                if (drains is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Agent drain is not configured."));
                                await drains.AcceptAsync(session, message.DrainCompleted, token);
                                break;
                            case AgentMessage.PayloadOneofCase.CheckpointSaved:
                                if (checkpoints is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Checkpoint persistence is not configured."));
                                await checkpoints.AcceptAsync(session, message.CheckpointSaved, token);
                                break;
                            case AgentMessage.PayloadOneofCase.ResourceSample:
                                if (resources is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Resource persistence is not configured."));
                                await resources.AcceptAsync(session, message.ResourceSample, token);
                                break;
                            case AgentMessage.PayloadOneofCase.Heartbeat:
                                if (!await registry.HeartbeatAsync(session, message.Heartbeat, token))
                                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Agent connection was superseded by a new Hello."));
                                break;
                            case AgentMessage.PayloadOneofCase.TaskStarted:
                            case AgentMessage.PayloadOneofCase.TaskCompleted:
                            case AgentMessage.PayloadOneofCase.TaskProgress:
                                if (runs is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Task dispatch persistence is not configured."));
                                await runs.AcceptAsync(session, message, token);
                                break;
                            case AgentMessage.PayloadOneofCase.ReservationFenced:
                                if (reservations is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Reservation persistence is not configured."));
                                await reservations.ConfirmAsync(session, message.ReservationFenced.ReservationEpoch, token);
                                break;
                            case AgentMessage.PayloadOneofCase.CalibrationCompleted:
                                if (calibrations is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Calibration persistence is not configured."));
                                await calibrations.AcceptAsync(session, message.CalibrationCompleted, token);
                                break;
                            case AgentMessage.PayloadOneofCase.Ack:
                                if (message.Ack.Sequence == 0 || message.Ack.Sequence > (ulong)Volatile.Read(ref sequence) ||
                                    message.Ack.LeaderEpoch != epoch)
                                    throw Invalid("Invalid control acknowledgement.");
                                received = message.Sequence;
                                continue;
                            default:
                                throw Invalid("Unsupported agent payload after Hello.");
                        }
                        received = message.Sequence;
                        await acknowledgements.Writer.WriteAsync(received, token);
                        wake.Writer.TryWrite(0);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }

            async Task SendAsync()
            {
                var sent = new HashSet<long>();
                var sentCancels = new HashSet<string>(StringComparer.Ordinal);
                var sentFences = new HashSet<ulong>();
                var sentCalibrations = new HashSet<string>(StringComparer.Ordinal);
                var sentDrains = new HashSet<string>(StringComparer.Ordinal);
                var sentMirrors = new Dictionary<string, MirrorLogs>(StringComparer.Ordinal);
                var nextMirrors = DateTimeOffset.MinValue;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        while (wake.Reader.TryRead(out _)) { }
                        CheckLeadership();
                        while (acknowledgements.Reader.TryRead(out var accepted))
                            await responseStream.WriteAsync(new ControlMessage
                            {
                                Sequence = checked((ulong)Interlocked.Increment(ref sequence)), LeaderEpoch = epoch,
                                Ack = new Ack { Sequence = accepted }
                            }, token);
                        if (reservations is not null)
                            foreach (var fence in await reservations.PendingAsync(session, token))
                                if (sentFences.Add(fence.ReservationEpoch))
                                    await responseStream.WriteAsync(new ControlMessage
                                    {
                                        Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                        LeaderEpoch = epoch, ReservationFence = fence
                                    }, token);
                        if (first.Hello.SupportsLogMirroring && liveLogs is not null && DateTimeOffset.UtcNow >= nextMirrors)
                        {
                            foreach (var mirror in await liveLogs.PendingAsync(session, sentMirrors, token))
                            {
                                await responseStream.WriteAsync(new ControlMessage
                                {
                                    Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                    LeaderEpoch = epoch, MirrorLogs = mirror
                                }, token);
                                var id = LiveLogProtocol.Identity(mirror.Task);
                                if (mirror.On) sentMirrors[id] = mirror;
                                else sentMirrors.Remove(id);
                            }
                            nextMirrors = DateTimeOffset.UtcNow + LiveLogProtocol.BatchInterval;
                        }
                        if (calibrations is not null)
                            foreach (var calibration in await calibrations.PendingAsync(session, token))
                                if (sentCalibrations.Add(calibration.Id))
                                    await responseStream.WriteAsync(new ControlMessage
                                    {
                                        Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                        LeaderEpoch = epoch, Calibrate = calibration
                                    }, token);
                        if (drains is not null && await drains.PendingAsync(session, token) is { } drain &&
                            sentDrains.Add($"{drain.Id}/{drain.ReservationEpoch}"))
                            await responseStream.WriteAsync(new ControlMessage
                            {
                                Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                LeaderEpoch = epoch, Drain = drain
                            }, token);
                        if (runs is not null)
                        {
                            var pending = await runs.PendingControlsAsync(session, token);
                            foreach (var (id, command) in pending.Dispatches)
                                if (sent.Add(id))
                                {
                                    if (logUploads is not null && command.Grants.All(grant => grant.Kind != "logs"))
                                        command.Grants.Add(logUploads.Create(command.Task, command.DeadlineAt.ToDateTimeOffset()));
                                    if (reportUploads is not null && command.Grants.All(grant => grant.Kind != "reports"))
                                        command.Grants.Add(reportUploads.Create(command.Task, command.DeadlineAt.ToDateTimeOffset()));
                                    if (artifactUploads is not null && command.Grants.All(grant => grant.Kind != "artifacts"))
                                        command.Grants.Add(artifactUploads.Create(command, session.AgentId, session.SessionId));
                                    if (checkpointUploads is not null && command.Grants.All(grant => grant.Kind != "checkpoints"))
                                        command.Grants.Add(checkpointUploads.Create(command, session));
                                    if (leaseOptions is not null)
                                        foreach (var grant in command.Grants)
                                            grant.UploadUri = new Uri(leaseOptions.Endpoint, grant.UploadUri).AbsoluteUri;
                                    if (checkpoints is not null)
                                        command.Checkpoint = await checkpoints.LoadForDispatchAsync(command, session, token);
                                    await responseStream.WriteAsync(new ControlMessage
                                    {
                                        Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                        LeaderEpoch = epoch, Dispatch = command
                                    }, token);
                                }
                            foreach (var cancel in pending.Cancels)
                                if (sentCancels.Add($"{cancel.Task.RunId}/{cancel.Task.NodeKey}/{cancel.Task.TaskKey}/{cancel.Task.Attempt}"))
                                    await responseStream.WriteAsync(new ControlMessage
                                    {
                                        Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                        LeaderEpoch = epoch, Cancel = cancel
                                    }, token);
                        }
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(TimeSpan.FromMilliseconds(25));
                        try { _ = await wake.Reader.WaitToReadAsync(wait.Token); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }

            void CheckLeadership()
            {
                if (leadership is not null && leadership.Current != lease)
                    throw new NotLeaderException(leadership.LeaderHint);
            }
        }
        catch (NotLeaderException exception)
        {
            logger.LogInformation("Agent connection redirected because this engine is not leader.");
            await responseStream.WriteAsync(new ControlMessage
            {
                Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                NotLeader = new NotLeader { LeaderHint = exception.LeaderHint }
            }, context.CancellationToken);
        }
        catch (ArgumentException exception)
        {
            logger.LogWarning("Rejected invalid agent protocol input: {Reason}", exception.Message);
            throw Invalid(exception.Message);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogWarning("Rejected agent registration with a conflicting identity.");
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Agent name belongs to a different agent ID."));
        }
    }

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));
}
