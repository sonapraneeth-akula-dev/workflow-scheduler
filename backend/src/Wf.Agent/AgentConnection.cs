using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent;

public interface IAgentCommandHandler
{
    ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken);
}

public sealed class AgentConnection(AgentOptions options, IAgentTelemetry telemetry,
    IAgentCommandHandler commands, TimeProvider timeProvider, ILogger<AgentConnection> logger)
{
    private long sequence;

    private ulong NextSequence() => checked((ulong)Interlocked.Increment(ref sequence));

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var index = 0;
        Uri? redirect = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var endpoint = redirect ?? options.Engines[index];
                redirect = null;
                try
                {
                    redirect = await ConnectAsync(endpoint, stoppingToken);
                    logger.LogWarning("Agent stream closed; reconnecting to a configured engine.");
                }
                catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded
                    or StatusCode.Cancelled or StatusCode.Internal or StatusCode.Unknown)
                {
                    logger.LogWarning("Agent connection lost ({StatusCode}); retrying a configured engine.", exception.StatusCode);
                }
                catch (HttpRequestException exception)
                {
                    logger.LogWarning("Agent HTTP connection failed ({Error}); retrying a configured engine.", exception.HttpRequestError);
                }
                index = (index + 1) % options.Engines.Count;
                await Task.Delay(redirect is null ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(100), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<Uri?> ConnectAsync(Uri endpoint, CancellationToken stoppingToken)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        };
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            HttpHandler = handler, DisposeHttpClient = true, MaxReceiveMessageSize = 1024 * 1024, MaxSendMessageSize = 1024 * 1024
        });
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: session.Token);
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = NextSequence(), Hello = telemetry.CreateHello(options) }, session.Token);
        logger.LogInformation("Agent Hello sent.");
        var sending = SendHeartbeatsAsync();
        var receiving = ReceiveAsync();
        await Task.WhenAny(sending, receiving);
        await session.CancelAsync();
        await Task.WhenAll(sending, receiving);
        return await receiving;

        async Task SendHeartbeatsAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(options.HeartbeatInterval, timeProvider);
                while (await timer.WaitForNextTickAsync(session.Token))
                    await call.RequestStream.WriteAsync(new AgentMessage
                    {
                        Sequence = NextSequence(), Heartbeat = telemetry.Sample()
                    }, session.Token);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch (RpcException exception) when (session.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled) { }
        }

        async Task<Uri?> ReceiveAsync()
        {
            ulong epoch = 0;
            ulong received = 0;
            try
            {
                await foreach (var message in call.ResponseStream.ReadAllAsync(session.Token))
                {
                    if (message.PayloadCase == ControlMessage.PayloadOneofCase.NotLeader)
                    {
                        if (!Uri.TryCreate(message.NotLeader.LeaderHint, UriKind.Absolute, out var hint) ||
                            !options.Engines.Contains(hint))
                        {
                            logger.LogWarning("Ignoring an absent or unconfigured leader hint; retrying configured engines.");
                            return null;
                        }
                        return hint;
                    }
                    if (message.Sequence == 0 || message.LeaderEpoch == 0 ||
                        (epoch != 0 && message.LeaderEpoch != epoch))
                        throw new InvalidDataException("Invalid control sequence or leader epoch.");
                    epoch = message.LeaderEpoch;
                    if (message.Sequence <= received) continue;
                    received = message.Sequence;
                    if (message.PayloadCase == ControlMessage.PayloadOneofCase.Ack)
                    {
                        if (message.Ack.Sequence == 0 || message.Ack.Sequence > (ulong)Volatile.Read(ref sequence) || message.Ack.LeaderEpoch != 0)
                            throw new InvalidDataException("Invalid acknowledgement of agent messages.");
                        continue;
                    }
                    if (message.PayloadCase == ControlMessage.PayloadOneofCase.None)
                        throw new InvalidDataException("Control message has no supported payload.");
                    await commands.HandleAsync(message, session.Token);
                }
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch (RpcException exception) when (session.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled) { }
            return null;
        }
    }
}
