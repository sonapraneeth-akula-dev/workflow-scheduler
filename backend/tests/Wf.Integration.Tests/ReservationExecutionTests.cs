using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Storage;
using Wf.TaskHost.Fixture;

namespace Wf.Integration.Tests;

public sealed partial class ReservationManagerTests
{
    [Fact(Timeout = 90_000)]
    public async Task Actual_HTTP2_agent_and_SDK_host_are_physically_stopped_before_expiry_releases_the_machine()
    {
        var bytes = await ReservationPackage();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var packageBuilder = WebApplication.CreateBuilder();
        packageBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var packageServer = packageBuilder.Build();
        packageServer.MapGet("/package.zip", () => Results.Bytes(bytes, "application/zip"));
        await packageServer.StartAsync(Token);
        using var registry = new AgentRegistry(database.DataSource, clock);
        using var leases = Leases;
        var root = Path.Combine(Path.GetTempPath(), "wf-rsv-" + Guid.NewGuid().ToString("N")[..12]);
        using var blobs = new FileSystemBlobStore(Path.Combine(root, "resources"), RandomNumberGenerator.GetBytes(32));
        var gatewayBuilder = WebApplication.CreateBuilder();
        gatewayBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        gatewayBuilder.Services.AddGrpc();
        gatewayBuilder.Services.AddSingleton(registry);
        gatewayBuilder.Services.AddSingleton(Store);
        gatewayBuilder.Services.AddSingleton(leases);
        gatewayBuilder.Services.AddSingleton(new ResourceSampleSink(database.DataSource, Store, blobs));
        await using var gateway = gatewayBuilder.Build();
        gateway.MapGrpcService<AgentGatewayService>();
        await gateway.StartAsync(Token);
        try
        {
            using var cache = new PackageCache(Path.Combine(root, "cache"), NullLogger<PackageCache>.Instance);
            await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
                containmentDirectory: Path.Combine(root, "host-lifetimes"));
            using var outbox = new AgentOutbox(new ResultJournal(Path.Combine(root, "journal"), Guid.NewGuid().ToString()));
            var id = Guid.NewGuid();
            var options = new AgentOptions([ReservationAddress(gateway)], id.ToString(), Name(id), id.ToString("N"), 1,
                heartbeatInterval: TimeSpan.FromMilliseconds(50));
            await using var commands = new TaskCommands(options, cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);
            var hold = new HeldReservationFence(commands);
            using var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System, commands), hold,
                TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var connected = connection.RunAsync(stop.Token);
            try
            {
                await ReservationWait(() => Task.FromResult(registry.Find(id) is not null), connected);
                var session = new AgentSession(id, registry.Find(id)!.SessionId);
                var run = await Ready("block");
                var reservation = Assert.Single(await Manager.ReserveAsync(run.Snapshot.RunId, "work", 1,
                    new(machineIds: [id]), Lifetime, Token));
                var uri = new Uri(ReservationAddress(packageServer), "/package.zip");
                using var package = await cache.GetAsync("1.0.0", uri, sha, Token);
                await hosts.PrewarmAsync(package, Token);
                var pid = Assert.Single(hosts.WarmProcessIds);
                output.WriteLine($"Actual task-host PID {pid}; reservation {reservation.Id:D}, epoch {reservation.Epoch}.");
                using var host = Process.GetProcessById(pid);
                using var dispatcher = new Dispatcher(database.DataSource, Store, registry, new(), clock);
                var command = new Dispatch
                {
                    Task = new() { RunId = run.Snapshot.RunId.ToString(), NodeKey = "work", TaskKey = "block", Attempt = 1 },
                    Version = "1.0.0", Sha256 = sha, PackageUri = uri.ToString(),
                    DeadlineAt = Timestamp.FromDateTimeOffset(clock.Now.AddHours(1))
                };
                Assert.NotNull((await dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, "work", "block", 1, sha,
                    new(new(machineIds: [id]))), command, Token)).Agent);
                await ReservationWait(async () => (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/block"] ==
                    ExecutionStatus.Running && commands.Inflight.Count == 1, connected);
                Assert.False(host.HasExited);
                clock.Now = reservation.ExpiresAt;
                await leases.SweepAsync(Token);
                await hold.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.False(host.HasExited);
                Assert.False((await ReservationRow(reservation.Id)).Released);
                Assert.Single(await leases.PendingAsync(session, Token));
                output.WriteLine("Expiry cancellation committed; live host held before physical fence, machine still reserved.");
                var foreign = await Ready("selfcancel");
                Assert.Null((await Send(dispatcher, foreign, "work", "selfcancel", new(machineIds: [id]))).Agent);
                hold.Continue.TrySetResult();
                await ReservationWait(async () => (await ReservationRow(reservation.Id)).Released, connected);
                await host.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
                output.WriteLine("Physical host exited and exact fence acknowledged; reservation released.");
                Assert.Empty(commands.Inflight);
                Assert.Equal(ExecutionStatus.Cancelled, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/block"]);
                var fenced = (await ReservationRow(reservation.Id)).Fence;
                await ReservationWait(() => Task.FromResult(registry.Find(id)!.SlotsUsed == 0), connected);
                command.Task = new() { RunId = foreign.Snapshot.RunId.ToString(), NodeKey = "work", TaskKey = "selfcancel", Attempt = 1 };
                Assert.NotNull((await dispatcher.DispatchAsync(foreign, new(foreign.Snapshot.RunId, "work", "selfcancel", 1, sha,
                    new(new(machineIds: [id]))), command, Token)).Agent);
                await ReservationWait(async () => (await Store.LoadAsync(foreign.Snapshot.RunId, Token)).Snapshot.Tasks["work/selfcancel"] ==
                    ExecutionStatus.Failed, connected);
                Assert.Equal(fenced, await Scalar("SELECT reservation_epoch FROM task_attempts WHERE run_id=$1", foreign.Snapshot.RunId));
                Assert.Equal(1L, await Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NOT NULL", run.Snapshot.RunId));
                Assert.Equal(2, hosts.ProcessStartCount);
            }
            finally
            {
                hold.Continue.TrySetResult();
                await stop.CancelAsync();
                await connected.WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class HeldReservationFence(TaskCommands commands) : IAgentCommandHandler
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken)
        {
            if (message.ReservationFence is not null)
            {
                Received.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            await commands.HandleAsync(message, cancellationToken);
        }
    }

    private static Uri ReservationAddress(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private static async Task ReservationWait(Func<Task<bool>> condition, Task connected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (connected.IsCompleted) { await connected; throw new InvalidOperationException("Agent connection stopped."); }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<byte[]> ReservationPackage()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory)
                         .Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                var entry = archive.CreateEntry(Path.GetFileName(path));
                await using var source = File.OpenRead(path);
                await using var destination = entry.Open();
                await source.CopyToAsync(destination, Token);
            }
            var manifest = archive.CreateEntry(PackageCache.ManifestName);
            await using var writer = new StreamWriter(manifest.Open(), Encoding.UTF8);
            await writer.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""".AsMemory(), Token);
        }
        return memory.ToArray();
    }
}
