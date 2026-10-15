using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts.Agent.V1;

namespace Wf.Integration.Tests.Agents;

public sealed class AgentGatewayContractTests
{
    [Fact]
    public async Task Connect_exchanges_all_payloads_over_real_http2_before_request_completion()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var app = builder.Build();
        app.MapGrpcService<ContractGateway>();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var channel = GrpcChannel.ForAddress(address);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: deadline.Token);

        var requests = new AgentMessage[]
        {
            new() { Sequence = 1, Hello = new Hello { Id = "agent-1", ProtocolVersion = 1 } },
            new() { Sequence = 2, Heartbeat = new Heartbeat { SlotsUsed = 1 } },
            new() { Sequence = 3, TaskStarted = new TaskStarted { Task = ContractGateway.Attempt } },
            new() { Sequence = 4, TaskCompleted = new TaskCompleted
            {
                Task = ContractGateway.Attempt, Outcome = TaskOutcome.Succeeded,
                OutputsJson = ByteString.CopyFromUtf8("""{"value":42}""")
            } },
            new() { Sequence = 5, Ack = new Ack { Sequence = 4, LeaderEpoch = 9 } }
        };
        var expected = new[]
        {
            ControlMessage.PayloadOneofCase.Dispatch, ControlMessage.PayloadOneofCase.Cancel,
            ControlMessage.PayloadOneofCase.Drain, ControlMessage.PayloadOneofCase.Ack,
            ControlMessage.PayloadOneofCase.NotLeader
        };
        for (var i = 0; i < requests.Length; i++)
        {
            await call.RequestStream.WriteAsync(requests[i], deadline.Token);
            Assert.True(await call.ResponseStream.MoveNext(deadline.Token));
            var response = call.ResponseStream.Current;
            Assert.Equal(expected[i], response.PayloadCase);
            Assert.Equal(requests[i].Sequence, response.Sequence);
            Assert.Equal(9UL, response.LeaderEpoch);
            if (response.Dispatch is { } dispatch)
            {
                Assert.Equal(ContractGateway.Attempt, dispatch.Task);
                Assert.Equal("""{"env":"dev"}""", dispatch.VariablesJson.ToStringUtf8());
            }
            if (response.Ack is { } ack) Assert.Equal(4UL, ack.Sequence);
        }
        await call.RequestStream.CompleteAsync();
        Assert.False(await call.ResponseStream.MoveNext(deadline.Token));
    }

    public sealed class ContractGateway : AgentGateway.AgentGatewayBase
    {
        public static AttemptRef Attempt => new() { RunId = Guid.Empty.ToString(), NodeKey = "build", TaskKey = "compile", Attempt = 1 };

        public override async Task Connect(IAsyncStreamReader<AgentMessage> requestStream,
            IServerStreamWriter<ControlMessage> responseStream, ServerCallContext context)
        {
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                var response = new ControlMessage { Sequence = message.Sequence, LeaderEpoch = 9 };
                switch (message.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Hello:
                        response.Dispatch = new Dispatch
                        {
                            Task = Attempt, Version = "1.0.0", Sha256 = new string('a', 64),
                            PackageUri = "https://localhost/package",
                            VariablesJson = ByteString.CopyFromUtf8("""{"env":"dev"}""")
                        };
                        break;
                    case AgentMessage.PayloadOneofCase.Heartbeat:
                        response.Cancel = new Cancel { Task = Attempt, GraceMs = 30000, Reason = "Aborted" };
                        break;
                    case AgentMessage.PayloadOneofCase.TaskStarted:
                        response.Drain = new Drain { Reason = "Maintenance" };
                        break;
                    case AgentMessage.PayloadOneofCase.TaskCompleted:
                        Assert.Equal("""{"value":42}""", message.TaskCompleted.OutputsJson.ToStringUtf8());
                        response.Ack = new Ack { Sequence = message.Sequence };
                        break;
                    case AgentMessage.PayloadOneofCase.Ack:
                        response.NotLeader = new NotLeader { LeaderHint = "https://leader:7443" };
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing payload."));
                }
                await responseStream.WriteAsync(response, context.CancellationToken);
            }
        }
    }
}
