using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class AgentConnectionTransportTests
{
    [Fact]
    public async Task Silent_peer_cannot_hold_registration_and_prevent_trying_the_next_engine()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(18));
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        using var next = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        next.Start();
        using var outbox = new AgentOutbox();
        using var connection = new AgentConnection(new AgentOptions(
            [Address(silent), Address(next)], Guid.NewGuid().ToString(), "transport", "tests", 1),
            new AgentTelemetry(TimeProvider.System), new NoCommands(), TimeProvider.System,
            NullLogger<AgentConnection>.Instance, outbox);
        var running = connection.RunAsync(stop.Token);
        try
        {
            using var stalled = await silent.AcceptTcpClientAsync(stop.Token);
            var preface = new byte[24];
            await stalled.GetStream().ReadExactlyAsync(preface, stop.Token);
            using var retried = await next.AcceptTcpClientAsync(stop.Token);
            await retried.GetStream().ReadExactlyAsync(preface, stop.Token);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync();
            silent.Stop();
            next.Stop();
            await running;
        }

        static Uri Address(TcpListener listener) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
    }

    [Fact]
    public async Task Peer_EOF_during_HTTP2_handshake_retries_without_stopping_the_agent()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}");
        using var outbox = new AgentOutbox();
        using var connection = new AgentConnection(new AgentOptions([endpoint], Guid.NewGuid().ToString(), "transport", "tests", 1),
            new AgentTelemetry(TimeProvider.System), new NoCommands(), TimeProvider.System,
            NullLogger<AgentConnection>.Instance, outbox);
        var failures = FailTwoHandshakes();
        var running = connection.RunAsync(stop.Token);
        try
        {
            var first = await Task.WhenAny(failures, running).WaitAsync(stop.Token);
            if (first == running) await running;
            await failures;
            Assert.False(running.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync();
            server.Stop();
            await running;
        }

        async Task FailTwoHandshakes()
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var socket = await server.AcceptTcpClientAsync(stop.Token);
                var preface = new byte[24];
                await socket.GetStream().ReadExactlyAsync(preface, stop.Token);
                Assert.Equal("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", System.Text.Encoding.ASCII.GetString(preface));
            }
        }
    }

    private sealed class NoCommands : IAgentCommandHandler
    {
        public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No control messages can pass an incomplete HTTP/2 handshake.");
    }
}
