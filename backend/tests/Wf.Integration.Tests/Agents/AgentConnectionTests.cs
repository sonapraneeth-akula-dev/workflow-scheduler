using System.Diagnostics;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Contracts.Agent.V1;

namespace Wf.Integration.Tests.Agents;

public sealed class AgentConnectionTests
{
    private const string Id = "f2111fd3-f78e-455b-bbe0-75aeae01fdd1";
    [Fact]
    public async Task Hello_precedes_live_heartbeats_and_duplicate_commands_execute_once()
    {
        var messages = new TaskCompletionSource<AgentMessage[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = await Gateway.Start(async (requests, responses, context) =>
        {
            var received = new List<AgentMessage>();
            await foreach (var message in requests.ReadAllAsync(context.CancellationToken))
            {
                received.Add(message);
                if (received.Count == 1)
                {
                    var cancel = new ControlMessage
                    {
                        Sequence = 1, LeaderEpoch = 9, Cancel = new Cancel { Reason = "Test" }
                    };
                    await responses.WriteAsync(cancel, context.CancellationToken);
                    await responses.WriteAsync(cancel, context.CancellationToken);
                }
                if (received.Count == 3) messages.TrySetResult(received.ToArray());
            }
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var count = 0;
        var connection = Create(gateway.Address, new Commands(_ => Interlocked.Increment(ref count)));
        var running = connection.RunAsync(cancellation.Token);
        try
        {
            var received = await messages.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(AgentMessage.PayloadOneofCase.Hello, received[0].PayloadCase);
            Assert.Equal(Id, received[0].Hello.Id);
            Assert.True(received[0].Hello.RamMb > 0);
            Assert.Equal(1U, received[0].Hello.ProtocolVersion);
            Assert.All(received[1..], item => Assert.Equal(AgentMessage.PayloadOneofCase.Heartbeat, item.PayloadCase));
            Assert.Equal(new ulong[] { 1, 2, 3 }, received.Select(item => item.Sequence));
            Assert.Equal(1, Volatile.Read(ref count));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Unacknowledged_results_replay_with_original_sequences_after_connection_loss()
    {
        var outbox = new AgentOutbox();
        outbox.Enqueue(new AgentMessage { TaskStarted = new TaskStarted { Task = new AttemptRef { TaskKey = "started" } } });
        outbox.Enqueue(new AgentMessage { TaskCompleted = new TaskCompleted { Outcome = TaskOutcome.Succeeded } });
        var replayed = new TaskCompletionSource<AgentMessage[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var gateway = await Gateway.Start(async (requests, responses, context) =>
        {
            var attempt = Interlocked.Increment(ref calls);
            var frames = new List<AgentMessage>();
            await foreach (var message in requests.ReadAllAsync(context.CancellationToken))
            {
                frames.Add(message);
                if (frames.Count != 3) continue;
                if (attempt == 1)
                    throw new RpcException(new Status(StatusCode.Unavailable, "Lost before durable result acknowledgement."));
                await responses.WriteAsync(new ControlMessage
                {
                    Sequence = 1, LeaderEpoch = 1, Ack = new Ack { Sequence = message.Sequence }
                }, context.CancellationToken);
                replayed.TrySetResult(frames.ToArray());
            }
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var options = new AgentOptions([gateway.Address], Id, "Test", "ci", 2);
        var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System), new Commands(_ => { }),
            TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
        var running = connection.RunAsync(cancellation.Token);
        try
        {
            var frames = await replayed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal(new ulong[] { 1, 2, 3 }, frames.Select(frame => frame.Sequence));
            Assert.NotNull(frames[0].Hello);
            Assert.Equal("started", frames[1].TaskStarted.Task.TaskKey);
            Assert.Equal(TaskOutcome.Succeeded, frames[2].TaskCompleted.Outcome);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Configured_leader_hint_redirects_and_preserves_sequence()
    {
        var leaderHello = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong standbySequence = 0;
        await using var leader = await Gateway.Start(async (requests, _, context) =>
        {
            await foreach (var message in requests.ReadAllAsync(context.CancellationToken))
                if (message.Hello is not null) leaderHello.TrySetResult(message);
        });
        await using var standby = await Gateway.Start(async (requests, responses, context) =>
        {
            if (await requests.MoveNext(context.CancellationToken))
            {
                standbySequence = requests.Current.Sequence;
                await responses.WriteAsync(new ControlMessage
                {
                    Sequence = 1, NotLeader = new NotLeader { LeaderHint = leader.Address.ToString() }
                }, context.CancellationToken);
            }
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var options = new AgentOptions([standby.Address, leader.Address], Id, "Test", "ci", 2);
        var running = Create(options).RunAsync(cancellation.Token);
        try
        {
            var message = await leaderHello.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(Id, message.Hello.Id);
            Assert.True(message.Sequence > standbySequence);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Unavailable_connection_retries_and_sends_a_new_Hello()
    {
        var reconnected = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var gateway = await Gateway.Start(async (requests, _, context) =>
        {
            if (!await requests.MoveNext(context.CancellationToken)) return;
            if (Interlocked.Increment(ref calls) == 1)
                throw new RpcException(new Status(StatusCode.Unavailable, "Simulated leader loss"));
            reconnected.TrySetResult(requests.Current);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = Create(gateway.Address).RunAsync(cancellation.Token);
        try
        {
            var hello = await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull(hello.Hello);
            Assert.True(hello.Sequence > 1);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("missing-payload")]
    [InlineData("missing-epoch")]
    [InlineData("future-ack")]
    [InlineData("stale-epoch")]
    public async Task Invalid_control_messages_stop_the_agent(string kind)
    {
        await using var gateway = await Gateway.Start(async (requests, responses, context) =>
        {
            if (!await requests.MoveNext(context.CancellationToken)) return;
            var ack = new ControlMessage { Sequence = 1, LeaderEpoch = 9, Ack = new Ack { Sequence = 1 } };
            if (kind == "missing-payload") ack.ClearPayload();
            if (kind == "missing-epoch") ack.LeaderEpoch = 0;
            if (kind == "future-ack") ack.Ack.Sequence = ulong.MaxValue;
            await responses.WriteAsync(ack, context.CancellationToken);
            if (kind == "stale-epoch")
                await responses.WriteAsync(new ControlMessage
                {
                    Sequence = 2, LeaderEpoch = 8, Ack = new Ack { Sequence = 1 }
                }, context.CancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(gateway.Address)
            .RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Permission_denied_is_fatal_instead_of_a_reconnect_loop()
    {
        await using var gateway = await Gateway.Start((_, _, _) =>
            throw new RpcException(new Status(StatusCode.PermissionDenied, "No Agent role")));
        var exception = await Assert.ThrowsAsync<RpcException>(() => Create(gateway.Address)
            .RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public async Task Unconfigured_leader_hint_cannot_redirect_agent_away_from_configured_engines()
    {
        var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var leader = await Gateway.Start(async (requests, _, context) =>
        {
            if (await requests.MoveNext(context.CancellationToken)) hello.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });
        await using var standby = await Gateway.Start(async (requests, responses, context) =>
        {
            if (!await requests.MoveNext(context.CancellationToken)) return;
            await responses.WriteAsync(new ControlMessage
            {
                Sequence = 1, NotLeader = new NotLeader { LeaderHint = "https://unconfigured.invalid" }
            }, context.CancellationToken);
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = Create(new AgentOptions([standby.Address, leader.Address], Id, "Test", "ci", 2))
            .RunAsync(cancellation.Token);
        try { await hello.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        finally
        {
            await cancellation.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Command_failure_is_surfaced_instead_of_acknowledged_or_retried()
    {
        await using var gateway = await Gateway.Start(async (requests, responses, context) =>
        {
            if (!await requests.MoveNext(context.CancellationToken)) return;
            await responses.WriteAsync(new ControlMessage
            {
                Sequence = 1, LeaderEpoch = 9, Dispatch = new Dispatch()
            }, context.CancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => Create(gateway.Address,
            new Commands(_ => throw new NotSupportedException("No task host installed.")))
            .RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Heartbeat_sampling_failure_stops_and_cancels_the_blocked_receiver()
    {
        await using var gateway = await Gateway.Start(async (requests, _, context) =>
        {
            await foreach (var message in requests.ReadAllAsync(context.CancellationToken)) { }
        });
        var options = new AgentOptions([gateway.Address], Id, "Test", "ci", 2,
            heartbeatInterval: TimeSpan.FromMilliseconds(50));
        var connection = new AgentConnection(options, new FailedTelemetry(), new Commands(_ => { }),
            TimeProvider.System, NullLogger<AgentConnection>.Instance);
        await Assert.ThrowsAsync<IOException>(() => connection.RunAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Published_native_agent_connects_and_sends_real_machine_heartbeats()
    {
        var executable = Environment.GetEnvironmentVariable("WF_AGENT_EXECUTABLE");
        Assert.SkipUnless(executable is not null, "Set WF_AGENT_EXECUTABLE to a published Native AOT agent.");
        Assert.True(File.Exists(executable), $"Published agent not found: {executable}");
        var received = new TaskCompletionSource<(Hello Hello, Heartbeat Heartbeat)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = await Gateway.Start(async (requests, _, context) =>
        {
            Hello? hello = null;
            await foreach (var message in requests.ReadAllAsync(context.CancellationToken))
            {
                hello ??= message.Hello;
                if (hello is not null && message.Heartbeat is { } heartbeat)
                    received.TrySetResult((hello, heartbeat));
            }
        });
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["Agent__Engines__0"] = gateway.Address.ToString();
        start.Environment["Agent__Id"] = Id;
        start.Environment["Agent__Labels__test"] = "aot";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var completed = await Task.WhenAny(received.Task, process.WaitForExitAsync(TestContext.Current.CancellationToken),
                Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.True(completed == received.Task, process.HasExited
                ? $"Agent exited {process.ExitCode}: {await stdout} {await stderr}" : "Agent did not send a heartbeat within 20 seconds.");
            var (hello, heartbeat) = await received.Task;
            Assert.Equal(Id, hello.Id);
            Assert.Equal("aot", hello.Labels["test"]);
            Assert.Equal(OperatingSystem.IsWindows() ? "windows" : "linux", hello.Os);
            Assert.True(hello.RamMb > 0);
            Assert.True(heartbeat.RamUsedMb > 0);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static AgentConnection Create(Uri address, IAgentCommandHandler? commands = null) =>
        Create(new AgentOptions([address], Id, "Test", "ci", 2,
            heartbeatInterval: TimeSpan.FromMilliseconds(50)), commands);

    private static AgentConnection Create(AgentOptions options, IAgentCommandHandler? commands = null) =>
        new(options, new AgentTelemetry(TimeProvider.System), commands ?? new Commands(_ => { }),
            TimeProvider.System, NullLogger<AgentConnection>.Instance);

    private sealed class Commands(Action<ControlMessage> action) : IAgentCommandHandler
    {
        public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken)
        {
            action(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailedTelemetry : IAgentTelemetry
    {
        public Hello CreateHello(AgentOptions options) => new() { Id = options.Id, ProtocolVersion = 1 };
        public Heartbeat Sample() => throw new IOException("Machine telemetry is unavailable.");
    }

    private sealed class Gateway(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static async Task<Gateway> Start(Func<IAsyncStreamReader<AgentMessage>, IServerStreamWriter<ControlMessage>, ServerCallContext, Task> connect)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(new TestService(connect));
            var app = builder.Build();
            app.MapGrpcService<TestService>();
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Gateway(app, new Uri(address));
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    public sealed class TestService(Func<IAsyncStreamReader<AgentMessage>, IServerStreamWriter<ControlMessage>, ServerCallContext, Task> connect)
        : AgentGateway.AgentGatewayBase
    {
        public override Task Connect(IAsyncStreamReader<AgentMessage> requestStream,
            IServerStreamWriter<ControlMessage> responseStream, ServerCallContext context) =>
            connect(requestStream, responseStream, context);
    }
}
