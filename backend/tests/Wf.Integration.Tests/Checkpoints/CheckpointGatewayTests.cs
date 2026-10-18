using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Wf.Agent;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Agents;

namespace Wf.Integration.Tests.Checkpoints;

public sealed partial class CheckpointApplierTests
{
    [Fact]
    public async Task Actual_HTTP2_checkpoint_ack_follows_durable_commit_and_lost_ack_replays_after_agent_journal_restart()
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("""{"position":42}""");
        using var registry = new AgentRegistry(database.DataSource, fixture.Runtime.Clock);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(fixture.Runtime.Store);
        builder.Services.AddSingleton(fixture.Applier);
        await using var gateway = builder.Build();
        gateway.MapGrpcService<AgentGatewayService>();
        await gateway.StartAsync(Token);
        var address = gateway.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var journalPath = Path.Combine(fixture.Root, "agent-journal");
        var id = fixture.Runtime.Session.AgentId.ToString("D");
        ulong sequence;
        using (var outbox = new AgentOutbox(new ResultJournal(journalPath, id)))
        {
            outbox.Enqueue(new() { CheckpointSaved = saved });
            sequence = outbox.NextAfter(1)!.Sequence;
            var acknowledgement = await Exchange(outbox);
            Assert.Equal(sequence, acknowledgement.Sequence);
            Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
            Assert.NotNull(outbox.NextAfter(1));
        }
        using (var outbox = new AgentOutbox(new ResultJournal(journalPath, id)))
        {
            Assert.Equal(sequence, outbox.NextAfter(1)!.Sequence);
            Assert.Equal(saved, outbox.NextAfter(1)!.CheckpointSaved);
            var acknowledgement = await Exchange(outbox);
            Assert.Equal(sequence, acknowledgement.Sequence);
            Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
            Assert.Equal(saved.BlobKey, await fixture.Runtime.Scalar("SELECT blob_uri FROM task_checkpoints WHERE run_id=$1"));
            outbox.Acknowledge(acknowledgement);
        }
        using var reopened = new AgentOutbox(new ResultJournal(journalPath, id));
        Assert.Null(reopened.NextAfter(1));

        async Task<Ack> Exchange(AgentOutbox outbox)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var channel = GrpcChannel.ForAddress(address);
            using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: timeout.Token);
            await call.RequestStream.WriteAsync(outbox.CreateHello(fixture.Hello()), timeout.Token);
            await call.RequestStream.WriteAsync(outbox.NextAfter(1)!, timeout.Token);
            while (await call.ResponseStream.MoveNext(timeout.Token))
                if (call.ResponseStream.Current.Ack is { } ack && ack.Sequence >= sequence)
                {
                    await call.RequestStream.CompleteAsync();
                    return ack;
                }
            throw new InvalidOperationException("The real gateway did not acknowledge the committed checkpoint.");
        }
    }
}
