using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Logs;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Logs;

public sealed class LiveLogIntegrationTests(LiveLogsDatabase database) : IClassFixture<LiveLogsDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_agent_grpc_to_separate_signed_MessagePack_APIs_is_interest_only_ordered_and_under_one_second()
    {
        await using var api = await TaskLogsHost.Start(database.DataSource, live: true);
        await using var otherApi = await TaskLogsHost.Start(database.DataSource, live: true, sharedStore: api.Store);
        var store = new FailingLiveLogStore(api.Store);
        await using var engine = await StartEngine(store);
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".live-log-agent", Guid.NewGuid().ToString("N"));
        var agent = Guid.NewGuid();
        var options = new AgentOptions([Address(engine)], agent.ToString(), $"live-{agent:N}", "ci", 1,
            heartbeatInterval: TimeSpan.FromMilliseconds(100), resultJournalDirectory: Path.Combine(root, "results"),
            logSpoolDirectory: Path.Combine(root, "spool"));
        using var outbox = new AgentOutbox();
        await using var mirror = new AgentLogMirror(outbox, TimeProvider.System, NullLogger<AgentLogMirror>.Instance);
        await using var captures = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance, mirror);
        var commands = new MirrorCommands(captures);
        using var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System), commands,
            TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = connection.RunAsync(stop.Token);
        try
        {
            await Until(() => Task.FromResult(engine.Services.GetRequiredService<AgentRegistry>().Find(agent) is not null), running);
            var session = engine.Services.GetRequiredService<AgentRegistry>().Find(agent)!.SessionId;
            var task = await Seed(api.Group, agent);
            await Grant(otherApi.Group, task.RunId);
            var capture = await captures.BeginAsync(new Dispatch { Task = task, ReservationEpoch = 1 });
            await capture.WriteAsync("stdout", "unwatched durable history");
            await Task.Delay(350, Token);
            Assert.Empty(LiveObjects(api, task));
            await using var first = await Viewer.Connect(api);
            await using var second = await Viewer.Connect(otherApi);
            var id = LiveLogProtocol.Identity(task);
            Assert.Null(await first.Mirror(id, true));
            Assert.Null(await second.Mirror(id, true));
            await commands.Read(on: true);
            while (first.Resyncs.Reader.TryRead(out _)) { }
            while (second.Resyncs.Reader.TryRead(out _)) { }
            store.FailNext = true;
            await capture.WriteAsync("stdout", "durable despite failed ephemeral upload");
            await first.Resyncs.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
            await second.Resyncs.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(1, store.Failures);
            Assert.False(first.Logs.Reader.TryPeek(out _));
            Assert.False(second.Logs.Reader.TryPeek(out _));
            var clock = Stopwatch.StartNew();
            await capture.WriteAsync("stderr", "<script>masked ***</script>");
            var a = await first.Read();
            var b = await second.Read();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Live tail lag was {clock.Elapsed.TotalMilliseconds:F1} ms.");
            Assert.Equal(id, a.Id);
            Assert.Equal(a, b with { Records = a.Records });
            Assert.Equal(a.Records, b.Records);
            var line = Assert.Single(a.Records);
            Assert.Equal(3, line.Line);
            Assert.Equal("<script>masked ***</script>", line.Text);
            Assert.Equal("stderr", line.Stream);
            Assert.Null(await first.Mirror(id, false));
            await Task.Delay(350, Token);
            Assert.DoesNotContain(commands.Seen, item => !item.On);
            await capture.WriteAsync("stdout", "only second API");
            Assert.Equal(4, Assert.Single((await second.Read()).Records).Line);
            Assert.False(first.Logs.Reader.TryPeek(out _));
            Assert.Null(await second.Mirror(id, false));
            await commands.Read(on: false);
            var count = LiveObjects(api, task).Length;
            await capture.WriteAsync("stdout", "unwatched again");
            await Task.Delay(350, Token);
            Assert.Equal(count, LiveObjects(api, task).Length);
            Assert.False(second.Logs.Reader.TryPeek(out _));
            await capture.CompleteAsync();
            await capture.Upload.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(session, engine.Services.GetRequiredService<AgentRegistry>().Find(agent)!.SessionId);
            Assert.False(running.IsCompleted);
            Assert.NotEmpty(LiveObjects(api, task));
            await Task.Delay(TimeSpan.FromSeconds(31), Token);
            Assert.Empty(LiveObjects(api, task));
            await using var inspect = database.DataSource.CreateCommand(
                "SELECT row_to_json(t)::text FROM task_attempts t WHERE run_id=$1");
            inspect.Parameters.AddWithValue(Guid.Parse(task.RunId));
            var persisted = (string)(await inspect.ExecuteScalarAsync(Token))!;
            Assert.DoesNotContain("masked", persisted);
            Assert.DoesNotContain("only second", persisted);
            Assert.DoesNotContain("<script>", persisted);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
            await captures.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Revoked_and_foreign_viewers_cannot_receive_log_bytes_and_stale_sessions_have_no_blob_side_effects()
    {
        await using var api = await TaskLogsHost.Start(database.DataSource, live: true);
        await using var engine = await StartEngine(api.Store);
        var registry = engine.Services.GetRequiredService<AgentRegistry>();
        var hello = Hello();
        var session = await registry.RegisterAsync(hello, Token);
        var task = await Seed(api.Group, session.AgentId);
        await using var viewer = await Viewer.Connect(api);
        var id = LiveLogProtocol.Identity(task);
        Assert.Null(await viewer.Mirror(id, true));
        var relay = engine.Services.GetRequiredService<LiveLogRelay>();
        var sent = new Dictionary<string, MirrorLogs>();
        await Until(async () => (await relay.PendingAsync(session, sent, Token)).Any(item => item.On));
        var sink = engine.Services.GetRequiredService<LiveLogSink>();
        var batch = Batch(task, "private masked bytes");
        await sink.AcceptAsync(session, batch, Token);
        Assert.Equal("private masked bytes", Assert.Single((await viewer.Read()).Records).Text);
        var foreign = task.Clone();
        foreign.RunId = Guid.CreateVersion7().ToString();
        Assert.Equal(403, (await viewer.Mirror(LiveLogProtocol.Identity(foreign), true))!.Status);
        await using (var revoke = database.DataSource.CreateCommand("DELETE FROM role_bindings WHERE principal_id=$1"))
        {
            revoke.Parameters.AddWithValue(api.Group);
            await revoke.ExecuteNonQueryAsync(Token);
        }
        batch.Records[0].Line = 2;
        batch.Records[0].Text = "revoked cannot see this";
        await sink.AcceptAsync(session, batch, Token);
        await Until(async () =>
        {
            await Task.CompletedTask;
            return viewer.Resyncs.Reader.TryRead(out _);
        });
        Assert.False(viewer.Logs.Reader.TryPeek(out _));
        Assert.Equal(403, (await viewer.Mirror(id, true))!.Status);
        var before = LiveObjects(api, task).Length;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sink.AcceptAsync(session with { SessionId = Guid.NewGuid() }, batch, Token));
        Assert.Equal(before, LiveObjects(api, task).Length);
        await using (var fence = database.DataSource.CreateCommand("UPDATE agents SET reservation_epoch=2 WHERE id=$1"))
        {
            fence.Parameters.AddWithValue(session.AgentId);
            await fence.ExecuteNonQueryAsync(Token);
        }
        batch.Records[0].Line = 3;
        await sink.AcceptAsync(session, batch, Token);
        Assert.Equal(before, LiveObjects(api, task).Length);
    }

    private async Task<WebApplication> StartEngine(IBlobStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AgentRegistry>();
        builder.Services.AddSingleton<RunStore>();
        builder.Services.AddWorkflowLive().AddWorkflowLiveLogs();
        var app = builder.Build();
        app.MapGrpcService<AgentGatewayService>();
        await app.StartAsync(Token);
        await Until(() => Task.FromResult(app.Services.GetRequiredService<LiveLogRelay>().IsReady));
        return app;
    }

    private async Task<AttemptRef> Seed(Guid group, Guid agent)
    {
        var team = "live" + Guid.NewGuid().ToString("N");
        var task = new AttemptRef { RunId = Guid.CreateVersion7().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 };
        var dispatch = new Dispatch { Task = task, ReservationEpoch = 1,
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)) };
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO teams(id,name) VALUES (@team,@team);
            INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
                VALUES (@team||'/type',@team,'type','type','live logs','{}');
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
                VALUES (@binding,1,@team,0,@group,0);
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,deadline_at)
                VALUES (uuid_v7_run_day(@run),@run,@team||'/type/workflow',@version,0,'live logs','{}','{}',2,now(),now()+interval '1 hour');
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,deadline_at,dispatch_payload,
                placement_request,execution_policy,reservation_epoch)
                VALUES (uuid_v7_run_day(@run),@run,'node/task',1,@agent,3,now()+interval '1 hour',@dispatch,'{}','{}',1);
            """);
        command.Parameters.AddWithValue("team", team);
        command.Parameters.AddWithValue("binding", Guid.CreateVersion7());
        command.Parameters.AddWithValue("group", group);
        command.Parameters.AddWithValue("run", Guid.Parse(task.RunId));
        command.Parameters.AddWithValue("version", Guid.CreateVersion7());
        command.Parameters.AddWithValue("agent", agent);
        command.Parameters.AddWithValue("dispatch", dispatch.ToByteArray());
        await command.ExecuteNonQueryAsync(Token);
        return task;
    }

    private async Task Grant(Guid group, string run)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
            SELECT $1,1,split_part(workflow_id,'/',1),0,$2,0 FROM runs WHERE id=$3
            """);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(group);
        command.Parameters.AddWithValue(Guid.Parse(run));
        await command.ExecuteNonQueryAsync(Token);
    }

    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new() { Id = id.ToString(), Name = $"live-{id:N}", Os = "linux", Arch = "x64",
            Cores = 1, RamMb = 1024, MaxTasks = 1, Pool = "ci", Version = "1.0", ProtocolVersion = 1, SupportsLogMirroring = true };
    }

    private static LiveLogBatch Batch(AttemptRef task, string text) => new()
    {
        Task = task, ReservationEpoch = 1,
        Records = { new LiveLogRecord { Line = 1, At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Stream = "stdout", Level = "Information", Text = text } }
    };

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static string[] LiveObjects(TaskLogsHost host, AttemptRef task)
    {
        var directory = host.BlobPath(LogIndex.PrefixFor(task));
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "live-*.log.gz") : [];
    }

    private static async Task Until(Func<Task<bool>> condition, Task? running = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (running?.IsCompleted == true) await running;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FailingLiveLogStore(IBlobStore inner) : IBlobStore
    {
        public volatile bool FailNext;
        public int Failures { get; private set; }
        public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default)
        {
            if (FailNext && content.Key.Contains("/live-", StringComparison.Ordinal))
            {
                FailNext = false;
                Failures++;
                throw new IOException("Injected ephemeral log storage failure.");
            }
            return inner.UploadAsync(content, source, cancellationToken);
        }
        public Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default) =>
            inner.DownloadAsync(content, destination, cancellationToken);
        public Task DeleteLiveLogAsync(string key, CancellationToken cancellationToken = default) =>
            inner.DeleteLiveLogAsync(key, cancellationToken);
    }

    private sealed class MirrorCommands(AgentLogService logs) : IAgentCommandHandler
    {
        private readonly Channel<MirrorLogs> commands = Channel.CreateUnbounded<MirrorLogs>();
        public ConcurrentQueue<MirrorLogs> Seen { get; } = new();
        public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken)
        {
            if (message.MirrorLogs is not { } mirror) throw new InvalidDataException("Unexpected test agent control.");
            logs.Mirror(mirror);
            Seen.Enqueue(mirror);
            if (!commands.Writer.TryWrite(mirror)) throw new InvalidOperationException("Test mirror queue closed.");
            return ValueTask.CompletedTask;
        }
        public async Task Read(bool on)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await commands.Reader.ReadAsync(timeout.Token)).On != on) { }
        }
    }

    private sealed class Viewer(HubConnection connection) : IAsyncDisposable
    {
        public Channel<LiveLogUpdate> Logs { get; } = Channel.CreateUnbounded<LiveLogUpdate>();
        public Channel<LiveResync> Resyncs { get; } = Channel.CreateUnbounded<LiveResync>();
        public static async Task<Viewer> Connect(TaskLogsHost host)
        {
            var connection = new HubConnectionBuilder().WithUrl(new Uri(host.Client.BaseAddress!, LiveHub.Path),
                options => { options.Transports = HttpTransportType.WebSockets; options.SkipNegotiation = true;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(host.Jwt()); })
                .AddMessagePackProtocol().Build();
            var viewer = new Viewer(connection);
            connection.On<LiveLogUpdate>("LogBatch", batch => viewer.Logs.Writer.TryWrite(batch));
            connection.On<LiveResync>("Resync", batch => viewer.Resyncs.Writer.TryWrite(batch));
            await connection.StartAsync(Token);
            return viewer;
        }
        public Task<LiveSubscriptionProblem?> Mirror(string id, bool on) =>
            connection.InvokeAsync<LiveSubscriptionProblem?>("MirrorLogs", id, on, Token);
        public async Task<LiveLogUpdate> Read() =>
            await Logs.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

public sealed class LiveLogsDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Migrations"), "*.sql")
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
}
