using Grpc.Core;
using Npgsql;
using Wf.Contracts.Agent.V1;

namespace Wf.ControlPlane.Engine.Agents;

public sealed class AgentGatewayService(AgentRegistry registry, ILogger<AgentGatewayService> logger)
    : AgentGateway.AgentGatewayBase
{
    public override async Task Connect(IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlMessage> responseStream, ServerCallContext context)
    {
        var token = context.CancellationToken;
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (!await requestStream.MoveNext(helloTimeout.Token)) return;
            var first = requestStream.Current;
            if (first.Sequence == 0 || first.Hello is null) throw Invalid("The first agent message must be a sequenced Hello.");
            var session = await registry.RegisterAsync(first.Hello, token);
            logger.LogInformation("Agent {AgentId} registered.", session.AgentId);
            ulong sequence = 0;
            var received = first.Sequence;
            await Acknowledge(received);
            await foreach (var message in requestStream.ReadAllAsync(token))
            {
                if (message.Sequence == 0) throw Invalid("Agent sequence must be positive.");
                if (message.Sequence <= received)
                {
                    if (message.PayloadCase != AgentMessage.PayloadOneofCase.Ack) await Acknowledge(received);
                    continue;
                }
                switch (message.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Heartbeat:
                        if (!await registry.HeartbeatAsync(session, message.Heartbeat, token))
                            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Agent connection was superseded by a new Hello."));
                        received = message.Sequence;
                        await Acknowledge(received);
                        break;
                    case AgentMessage.PayloadOneofCase.Ack:
                        if (message.Ack.Sequence == 0 || message.Ack.Sequence > sequence || message.Ack.LeaderEpoch != 1)
                            throw Invalid("Invalid control acknowledgement.");
                        received = message.Sequence;
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.Unimplemented, "This gateway currently accepts Hello, Heartbeat and Ack only."));
                }
            }

            Task Acknowledge(ulong accepted) => responseStream.WriteAsync(new ControlMessage
            {
                Sequence = ++sequence, LeaderEpoch = 1, Ack = new Ack { Sequence = accepted }
            }, token);
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
