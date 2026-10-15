using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.Scheduling;
using Wf.TaskHost.Fixture;

namespace Wf.Integration.Tests.Agents;

public sealed class DispatchTests(DispatchDatabase database) : IClassFixture<DispatchDatabase>
{
    private static readonly ImmutableArray<NodeDefinition> Nodes =
    [
        new("source", [], [new("noop")]),
        new("work", [new("source", DependencyCondition.OnSuccess)], [new("echo")])
    ];
    private readonly RunStore store = new(database.DataSource, NullLogger<RunStore>.Instance);
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task In_memory_Ready_cannot_dispatch_before_the_event_commits()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        using var dispatcher = CreateDispatcher(registry);
        var run = await CreateRun();
        run.Append(EventEntity.Task, "source/noop", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch(dispatcher, run, "source", "noop"));
        Assert.Equal(ExecutionStatus.Pending, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["source/noop"]);
        Assert.Empty(await store.PendingAsync(session, Token));
    }

    [Fact]
    public async Task Committed_dispatch_reserves_capacity_before_any_heartbeat_and_survives_dispatcher_recreation()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = Hello();
        hello.MaxTasks = 1;
        var session = await registry.RegisterAsync(hello, Token);
        var run = await CreateRun();
        await Ready(run, "source/noop");
        await Ready(run, "work/echo");
        using (var dispatcher = CreateDispatcher(registry))
        {
            Assert.Equal(session.AgentId, (await Dispatch(dispatcher, run, "source", "noop")).Agent!.Id);
            var revision = run.Snapshot.LastSequence;
            Assert.Null((await Dispatch(dispatcher, run, "work", "echo")).Agent);
            Assert.Equal(revision, run.Snapshot.LastSequence);
        }
        using var restarted = CreateDispatcher(registry);
        Assert.Null((await Dispatch(restarted, run, "work", "echo")).Agent);
        Assert.Single(await store.PendingAsync(session, Token));
        var loaded = await store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(ExecutionStatus.Dispatched, loaded.Snapshot.Tasks["source/noop"]);
        Assert.Equal(ExecutionStatus.Ready, loaded.Snapshot.Tasks["work/echo"]);
    }

    [Fact]
    public async Task Outbox_failure_rolls_back_event_attempt_snapshot_and_placement()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var placement = new PlacementEngine();
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, placement, TimeProvider.System);
        var run = await CreateRun();
        await Ready(run, "source/noop");
        var sequence = run.Snapshot.LastSequence;
        await Execute($"ALTER TABLE dispatch_outbox ADD CONSTRAINT reject_test CHECK (run_id <> '{run.Snapshot.RunId:D}')");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Dispatch(dispatcher, run, "source", "noop"));
            Assert.Equal(sequence, run.Snapshot.LastSequence);
            Assert.Equal(0, placement.Snapshot.Revision);
            Assert.Equal(ExecutionStatus.Ready, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["source/noop"]);
            Assert.Empty(await store.PendingAsync(session, Token));
            await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM task_attempts WHERE run_id=$1");
            count.Parameters.AddWithValue(run.Snapshot.RunId);
            Assert.Equal(0L, await count.ExecuteScalarAsync(Token));
        }
        finally { await Execute("ALTER TABLE dispatch_outbox DROP CONSTRAINT reject_test"); }
    }

    [Fact]
    public async Task Results_are_durable_idempotent_and_fenced_to_the_assigned_agent_session()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = Hello();
        var first = await registry.RegisterAsync(hello, Token);
        var run = await CreateRun();
        await Ready(run, "source/noop");
        using var dispatcher = CreateDispatcher(registry);
        await Dispatch(dispatcher, run, "source", "noop");
        var pending = Assert.Single(await store.PendingAsync(first, Token));
        var started = new AgentMessage
        {
            TaskStarted = new TaskStarted { Task = pending.Command.Task.Clone(), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) }
        };
        var second = await registry.RegisterAsync(hello, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptAsync(first, started, Token));
        await store.AcceptAsync(second, started, Token);
        var running = await store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(ExecutionStatus.Running, running.Snapshot.Tasks["source/noop"]);
        await store.AcceptAsync(second, started, Token);
        Assert.Equal(running.Snapshot.LastSequence, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        Assert.Empty(await store.PendingAsync(second, Token));
        var result = new AgentMessage
        {
            TaskCompleted = new TaskCompleted
            {
                Task = pending.Command.Task.Clone(), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Outcome = TaskOutcome.Succeeded, OutputsJson = ByteString.CopyFromUtf8("""{"value":7}""")
            }
        };
        await store.AcceptAsync(second, result, Token);
        var completed = await store.LoadAsync(run.Snapshot.RunId, Token);
        await store.AcceptAsync(second, result, Token);
        await store.AcceptAsync(second, started, Token);
        Assert.Equal(completed.Snapshot.LastSequence, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        var foreign = await registry.RegisterAsync(Hello(), Token);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcceptAsync(foreign, result, Token));
    }

    [Fact]
    public async Task Dependent_task_runs_on_real_warm_agent_and_second_run_downloads_nothing()
    {
        var bytes = await Package();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var downloads = 0;
        var packageBuilder = WebApplication.CreateBuilder();
        packageBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var packages = packageBuilder.Build();
        packages.MapGet("/package.zip", () =>
        {
            Interlocked.Increment(ref downloads);
            return Results.Bytes(bytes, "application/zip");
        });
        await packages.StartAsync(Token);
        var packageUri = new Uri(Address(packages), "/package.zip");
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var gatewayBuilder = WebApplication.CreateBuilder();
        gatewayBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        gatewayBuilder.Services.AddGrpc();
        gatewayBuilder.Services.AddSingleton(registry);
        gatewayBuilder.Services.AddSingleton(store);
        await using var gateway = gatewayBuilder.Build();
        gateway.MapGrpcService<AgentGatewayService>();
        await gateway.StartAsync(Token);
        var root = Path.Combine(Path.GetTempPath(), $"wf-dispatch-{Guid.NewGuid():N}");
        try
        {
            using var cache = new PackageCache(root, NullLogger<PackageCache>.Instance);
            await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
            var outbox = new AgentOutbox();
            var id = Guid.NewGuid();
            var options = new AgentOptions([Address(gateway)], id.ToString(), $"dispatch-{id:N}", "ci", 2,
                heartbeatInterval: TimeSpan.FromMilliseconds(50));
            await using var commands = new TaskCommands(options, cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);
            var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System, commands), commands,
                TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var connected = connection.RunAsync(stopping.Token);
            try
            {
                await Wait(async () => { await Task.CompletedTask; return registry.Find(id) is not null; }, connected);
                using var dispatcher = CreateDispatcher(registry);
                for (var index = 0; index < 2; index++)
                {
                    var package = await cache.GetAsync("1.0.0", packageUri, sha, Token);
                    await hosts.PrewarmAsync(package, Token);
                    await hosts.PrewarmAsync(package, Token);
                    var warm = hosts.WarmProcessIds.ToArray();
                    var starts = hosts.ProcessStartCount;
                    var run = await CreateRun();
                    await Ready(run, "source/noop");
                    await Dispatch(dispatcher, run, "source", "noop", sha, packageUri);
                    await Wait(async () => (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["source/noop"] == ExecutionStatus.Succeeded, connected);
                    run = await store.LoadAsync(run.Snapshot.RunId, Token);
                    await SetNode(run, "source", ExecutionStatus.Ready);
                    await SetNode(run, "source", ExecutionStatus.Dispatched);
                    await SetNode(run, "source", ExecutionStatus.Running);
                    await SetNode(run, "source", ExecutionStatus.Succeeded);
                    Assert.Equal(ExecutionStatus.Ready, ReadinessEvaluator.Evaluate(Nodes, run.Snapshot.Nodes).Single().Status);
                    await Ready(run, "work/echo");
                    var echo = Command(run, "work", "echo", sha, packageUri);
                    await using (var source = database.DataSource.CreateCommand("SELECT outputs::text FROM node_runs WHERE run_id=$1 AND node_key='source'"))
                    {
                        source.Parameters.AddWithValue(run.Snapshot.RunId);
                        echo.InputsJson = ByteString.CopyFromUtf8($$"""{"source":{{await source.ExecuteScalarAsync(Token)}}}""");
                    }
                    echo.VariablesJson = ByteString.CopyFromUtf8("""{"amount":5}""");
                    echo.NodeOutputsJson = ByteString.CopyFromUtf8("""{"previous":0}""");
                    await dispatcher.DispatchAsync(run, Request(run, "work", "echo", sha), echo, Token);
                    await Wait(async () => (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/echo"] == ExecutionStatus.Succeeded, connected);
                    await using var output = database.DataSource.CreateCommand("SELECT outputs::text FROM node_runs WHERE run_id=$1 AND node_key='work'");
                    output.Parameters.AddWithValue(run.Snapshot.RunId);
                    using var document = JsonDocument.Parse((string)(await output.ExecuteScalarAsync(Token))!);
                    Assert.Equal(12, document.RootElement.GetProperty("result").GetInt32());
                    Assert.Contains(document.RootElement.GetProperty("pid").GetInt32(), warm);
                    Assert.Equal(starts, hosts.ProcessStartCount);
                    Assert.Equal(1, Volatile.Read(ref downloads));
                    await using var events = database.DataSource.CreateCommand("""
                        SELECT payload FROM run_events WHERE run_id=$1 AND entity_key='work/echo' ORDER BY seq
                        """);
                    events.Parameters.AddWithValue(run.Snapshot.RunId);
                    await using var reader = await events.ExecuteReaderAsync(Token);
                    var states = new List<int>();
                    while (await reader.ReadAsync(Token))
                    {
                        using var change = JsonDocument.Parse(reader.GetFieldValue<byte[]>(0));
                        states.Add(change.RootElement.GetProperty("State").GetInt32());
                    }
                    Assert.Equal(new[] { 1, 2, 3, 5 }, states);
                }
            }
            finally
            {
                await stopping.CancelAsync();
                await connected.WaitAsync(TimeSpan.FromSeconds(5), Token);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private Dispatcher CreateDispatcher(AgentRegistry registry) =>
        new(database.DataSource, store, registry, new PlacementEngine(), TimeProvider.System);

    private async Task<RunState> CreateRun()
    {
        var run = new RunState(Guid.CreateVersion7(), Nodes,
            new WorkflowMetadata("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, "tests/dispatch", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        return run;
    }

    private Task Ready(RunState run, string key) =>
        store.AppendAsync(run, EventEntity.Task, key, (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);

    private Task SetNode(RunState run, string key, ExecutionStatus state) =>
        store.AppendAsync(run, EventEntity.Node, key, (int)state, DateTimeOffset.UtcNow, Token);

    private Task<PlacementDecision> Dispatch(Dispatcher dispatcher, RunState run, string node, string task,
        string sha = Sha, Uri? uri = null) =>
        dispatcher.DispatchAsync(run, Request(run, node, task, sha), Command(run, node, task, sha, uri), Token);

    private static PlacementRequest Request(RunState run, string node, string task, string sha) =>
        new(run.Snapshot.RunId, node, task, 1, sha);

    private static Wf.Contracts.Agent.V1.Dispatch Command(RunState run, string node, string task, string sha, Uri? uri) => new()
    {
        Task = new AttemptRef { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = node, TaskKey = task, Attempt = 1 },
        Version = "1.0.0", PackageUri = (uri ?? new Uri("https://packages.invalid/package.zip")).ToString(), Sha256 = sha,
        DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30))
    };

    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new Hello
        {
            Id = id.ToString(), Name = $"dispatch-{id:N}", Os = "linux", Arch = "x64", Cores = 2,
            RamMb = 8192, MaxTasks = 2, Pool = "ci", Version = "1.0", ProtocolVersion = 1
        };
    }

    private async Task Execute(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task Wait(Func<Task<bool>> condition, Task connected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (connected.IsCompleted) await connected;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()!.Addresses.Single());

    private async Task<byte[]> Package()
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

public sealed class DispatchDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql", "0011_attempt_replacement.sql"];
}
