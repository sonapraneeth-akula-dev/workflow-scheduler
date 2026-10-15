using Grpc.Core;
using Npgsql;
using System.Threading.Channels;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Dispatch;

namespace Wf.ControlPlane.Engine.Agents;

public sealed class AgentGatewayService(AgentRegistry registry, ILogger<AgentGatewayService> logger, RunStore? runs = null)
    : AgentGateway.AgentGatewayBase
{
    public override async Task Connect(IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlMessage> responseStream, ServerCallContext context)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var token = stopping.Token;
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (!await requestStream.MoveNext(helloTimeout.Token)) return;
            var first = requestStream.Current;
            if (first.Sequence == 0 || first.Hello is null) throw Invalid("The first agent message must be a sequenced Hello.");
            var session = await registry.RegisterAsync(first.Hello, token);
            logger.LogInformation("Agent {AgentId} registered.", session.AgentId);
            long sequence = 0;
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
                        if (message.Sequence == 0) throw Invalid("Agent sequence must be positive.");
                        if (message.Sequence <= received)
                        {
                            if (message.PayloadCase != AgentMessage.PayloadOneofCase.Ack)
                                await acknowledgements.Writer.WriteAsync(received, token);
                            continue;
                        }
                        switch (message.PayloadCase)
                        {
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
                            case AgentMessage.PayloadOneofCase.Ack:
                                if (message.Ack.Sequence == 0 || message.Ack.Sequence > (ulong)Volatile.Read(ref sequence) ||
                                    message.Ack.LeaderEpoch != 1)
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
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        while (acknowledgements.Reader.TryRead(out var accepted))
                            await responseStream.WriteAsync(new ControlMessage
                            {
                                Sequence = checked((ulong)Interlocked.Increment(ref sequence)), LeaderEpoch = 1,
                                Ack = new Ack { Sequence = accepted }
                            }, token);
                        if (runs is not null)
                            foreach (var (id, command) in await runs.PendingAsync(session, token))
                                if (sent.Add(id))
                                    await responseStream.WriteAsync(new ControlMessage
                                    {
                                        Sequence = checked((ulong)Interlocked.Increment(ref sequence)),
                                        LeaderEpoch = 1, Dispatch = command
                                    }, token);
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(TimeSpan.FromMilliseconds(25));
                        try { _ = await acknowledgements.Reader.WaitToReadAsync(wait.Token); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
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
