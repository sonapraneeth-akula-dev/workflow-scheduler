using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent;

public interface IAgentCommandHandler
{
    ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken);
}

public sealed class AgentConnection(AgentOptions options, IAgentTelemetry telemetry,
    IAgentCommandHandler commands, TimeProvider timeProvider, ILogger<AgentConnection> logger, AgentOutbox? outbox = null) : IDisposable
{
    private readonly AgentOutbox outgoing = outbox ?? new AgentOutbox(new ResultJournal(options.ResultJournalDirectory, options.Id));

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var index = 0;
        Uri? redirect = null;
        try
        {
            if (commands is TaskCommands tasks) await tasks.RecoverAsync(stoppingToken);
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
                catch (HttpIOException exception)
                {
                    logger.LogWarning("Agent HTTP stream failed ({Error}); retrying a configured engine.", exception.HttpRequestError);
                }
                index = (index + 1) % options.Engines.Count;
                await Task.Delay(redirect is null ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(100), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Dispose(); }
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
            HttpHandler = handler, DisposeHttpClient = true, MaxReceiveMessageSize = 4 * 1024 * 1024, MaxSendMessageSize = 1024 * 1024
        });
        using var registration = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, registration.Token);
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: session.Token);
        var inventory = telemetry.CreateHello(options);
        var attempts = inventory.Inflight.Concat(outgoing.UnacknowledgedAttempts())
            .DistinctBy(task => (Guid.Parse(task.RunId), task.NodeKey, task.TaskKey, task.Attempt)).ToArray();
        inventory.Inflight.Clear();
        inventory.Inflight.AddRange(attempts);
        var hello = outgoing.CreateHello(inventory);
        var offered = hello.Sequence;
        try { await call.RequestStream.WriteAsync(hello, session.Token); }
        catch (OperationCanceledException) when (registration.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Agent Hello was not acknowledged within 10 seconds; retrying a configured engine.");
            return null;
        }
        logger.LogInformation("Agent Hello sent.");
        var sending = SendAsync(hello.Sequence);
        var receiving = ReceiveAsync();
        var failure = (commands as TaskCommands)?.Failure;
        var completed = await (failure is null ? Task.WhenAny(sending, receiving) : Task.WhenAny(sending, receiving, failure));
        await session.CancelAsync();
        await Task.WhenAll(sending, receiving);
        if (completed == failure) await failure;
        if (registration.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            logger.LogWarning("Agent Hello was not acknowledged within 10 seconds; retrying a configured engine.");
        return await receiving;

        async Task SendAsync(ulong sent)
        {
            try
            {
                var nextHeartbeat = timeProvider.GetUtcNow() + options.HeartbeatInterval;
                while (!session.IsCancellationRequested)
                {
                    while (outgoing.NextAfter(sent) is { } message)
                    {
                        Volatile.Write(ref offered, message.Sequence);
                        await call.RequestStream.WriteAsync(message, session.Token);
                        sent = message.Sequence;
                    }
                    var remaining = nextHeartbeat - timeProvider.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                    {
                        outgoing.Enqueue(new AgentMessage { Heartbeat = telemetry.Sample() });
                        nextHeartbeat = timeProvider.GetUtcNow() + options.HeartbeatInterval;
                        continue;
                    }
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    wait.CancelAfter(remaining);
                    try { await outgoing.WaitForChangeAsync(wait.Token); }
                    catch (OperationCanceledException) when (!session.IsCancellationRequested) { }
                }
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
                            !AgentOptions.IsEngineOrigin(hint) ||
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
                    outgoing.ObserveLeaderEpoch(epoch);
                    if (message.Sequence <= received) continue;
                    received = message.Sequence;
                    if (message.PayloadCase == ControlMessage.PayloadOneofCase.Ack)
                    {
                        if (message.Ack.Sequence < hello.Sequence || message.Ack.Sequence > Volatile.Read(ref offered))
                            throw new InvalidDataException("Acknowledgement does not correspond to messages sent in this session.");
                        outgoing.Acknowledge(message.Ack);
                        registration.CancelAfter(Timeout.InfiniteTimeSpan);
                        continue;
                    }
                    if (message.PayloadCase == ControlMessage.PayloadOneofCase.None)
                        throw new InvalidDataException("Control message has no supported payload.");
                    await commands.HandleAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch (RpcException exception) when (session.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled) { }
            return null;
        }
    }

    public void Dispose()
    {
        if (outbox is null) outgoing.Dispose();
    }
}
