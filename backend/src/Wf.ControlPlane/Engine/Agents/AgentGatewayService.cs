using Grpc.Core;
using Npgsql;
using System.Threading.Channels;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Engine.Agents;

public sealed class AgentGatewayService(AgentRegistry registry, ILogger<AgentGatewayService> logger, RunStore? runs = null,
    LeaseManager? leadership = null, AgentLossSweeper? losses = null, LogUploadCapabilities? logUploads = null,
    Wf.ControlPlane.Reports.ReportUploadCapabilities? reportUploads = null,
    Wf.ControlPlane.Artifacts.ArtifactUploadCapabilities? artifactUploads = null, ResourceSampleSink? resources = null,
    ReservationLeaseService? reservations = null)
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
            await acknowledgements.Writer.WriteAsync(received, token);
            var sending = SendAsync();
            var receiving = ReceiveAsync();
            try
            {
                await Task.WhenAny(sending, receiving);
                await stopping.CancelAsync();
                await Task.WhenAll(sending, receiving);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }

            async Task ReceiveAsync()
            {
                try
                {
                    await foreach (var message in requestStream.ReadAllAsync(token))
                    {
                        CheckLeadership();
                        if (message.Sequence == 0) throw Invalid("Agent sequence must be positive.");
                        if (message.Sequence <= received)
                        {
                            if (message.PayloadCase != AgentMessage.PayloadOneofCase.Ack)
                                await acknowledgements.Writer.WriteAsync(received, token);
                            continue;
                        }
                        switch (message.PayloadCase)
                        {
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
                                if (runs is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Task dispatch persistence is not configured."));
                                await runs.AcceptAsync(session, message, token);
                                break;
                            case AgentMessage.PayloadOneofCase.ReservationFenced:
                                if (reservations is null)
                                    throw new RpcException(new Status(StatusCode.Unimplemented, "Reservation persistence is not configured."));
                                await reservations.ConfirmAsync(session, message.ReservationFenced.ReservationEpoch, token);
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
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }

            async Task SendAsync()
            {
                var sent = new HashSet<long>();
                var sentFences = new HashSet<ulong>();
                try
                {
                    while (!token.IsCancellationRequested)
                    {
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
                        if (runs is not null)
                            foreach (var (id, command) in await runs.PendingAsync(session, token))
                                if (sent.Add(id))
                                {
                                    if (logUploads is not null && command.Grants.All(grant => grant.Kind != "logs"))
                                        command.Grants.Add(logUploads.Create(command.Task, command.DeadlineAt.ToDateTimeOffset()));
                                    if (reportUploads is not null && command.Grants.All(grant => grant.Kind != "reports"))
                                        command.Grants.Add(reportUploads.Create(command.Task, command.DeadlineAt.ToDateTimeOffset()));
                                    if (artifactUploads is not null && command.Grants.All(grant => grant.Kind != "artifacts"))
                                        command.Grants.Add(artifactUploads.Create(command, session.AgentId, session.SessionId));
                                    await responseStream.WriteAsync(new ControlMessage
                                    {
                                        Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                        LeaderEpoch = epoch, Dispatch = command
                                    }, token);
                                }
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(TimeSpan.FromMilliseconds(25));
                        try { _ = await acknowledgements.Reader.WaitToReadAsync(wait.Token); }
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
