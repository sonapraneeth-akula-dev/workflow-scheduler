using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.Agent;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Agents;
using Wf.Engine.Core.Placement;

namespace Wf.Integration.Tests.Agents;

public sealed class RegistrationTests(AgentDatabase database) : IClassFixture<AgentDatabase>
{
    [Fact]
    public async Task Hello_is_durable_and_healthy_then_expires_at_exactly_fifteen_virtual_seconds()
    {
        var clock = new ManualClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var hello = Hello();
        var session = await registry.RegisterAsync(hello, TestContext.Current.CancellationToken);
        Assert.Equal(AgentStatus.Healthy, registry.Find(session.AgentId)!.Status);
        clock.Advance(TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(10));
        Assert.Contains(registry.DispatchableAgents(), item => item.Id == session.AgentId);
        await registry.SweepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentStatus.Healthy, registry.Find(session.AgentId)!.Status);
        clock.Advance(TimeSpan.FromTicks(10));
        Assert.DoesNotContain(registry.DispatchableAgents(), item => item.Id == session.AgentId);
        await registry.SweepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentStatus.Unhealthy, registry.Find(session.AgentId)!.Status);
        using var rebuilt = new AgentRegistry(database.DataSource, clock);
        await rebuilt.LoadAsync(TestContext.Current.CancellationToken);
        var expected = registry.Find(session.AgentId)!;
        var actual = rebuilt.Find(session.AgentId)!;
        Assert.Equal(expected.Labels.OrderBy(pair => pair.Key), actual.Labels.OrderBy(pair => pair.Key));
        Assert.Equal(expected, actual with { Labels = expected.Labels });
    }

    [Fact]
    public async Task Valid_heartbeat_recovers_health_and_persists_resource_metrics()
    {
        var clock = new ManualClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(15));
        await registry.SweepAsync(TestContext.Current.CancellationToken);
        Assert.True(await registry.HeartbeatAsync(session, Heartbeat(), TestContext.Current.CancellationToken));
        var agent = registry.Find(session.AgentId)!;
        Assert.Equal(AgentStatus.Healthy, agent.Status);
        Assert.Equal(clock.GetUtcNow(), agent.LastHeartbeatAt);
        Assert.Equal(25, agent.CpuPercent);
        Assert.Equal(512, agent.RamUsedMb);
        Assert.Equal(1, agent.SlotsUsed);
        using var rebuilt = new AgentRegistry(database.DataSource, clock);
        await rebuilt.LoadAsync(TestContext.Current.CancellationToken);
        var persisted = rebuilt.Find(session.AgentId)!;
        Assert.Equal(agent.SessionId, persisted.SessionId);
        Assert.Equal(agent.CpuPercent, persisted.CpuPercent);
        Assert.Equal(agent.SlotsUsed, persisted.SlotsUsed);
    }

    [Fact]
    public async Task Superseded_connection_cannot_refresh_a_new_session_or_its_database_record()
    {
        var clock = new ManualClock();
        using var firstRegistry = new AgentRegistry(database.DataSource, clock);
        using var secondRegistry = new AgentRegistry(database.DataSource, clock);
        var hello = Hello();
        var first = await firstRegistry.RegisterAsync(hello, TestContext.Current.CancellationToken);
        var second = await secondRegistry.RegisterAsync(hello, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.SessionId, second.SessionId);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await firstRegistry.HeartbeatAsync(first, Heartbeat(), TestContext.Current.CancellationToken));
        Assert.True(await secondRegistry.HeartbeatAsync(second, Heartbeat(), TestContext.Current.CancellationToken));
        await firstRegistry.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(second.SessionId, firstRegistry.Find(second.AgentId)!.SessionId);
    }

    [Fact]
    public async Task Duplicate_name_is_rejected_without_changing_cache_or_existing_identity()
    {
        using var registry = new AgentRegistry(database.DataSource, new ManualClock());
        var hello = Hello();
        var existing = await registry.RegisterAsync(hello, TestContext.Current.CancellationToken);
        var duplicate = hello.Clone();
        duplicate.Id = Guid.NewGuid().ToString();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => registry.RegisterAsync(duplicate, TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Null(registry.Find(Guid.Parse(duplicate.Id)));
        Assert.Equal(existing.SessionId, registry.Find(existing.AgentId)!.SessionId);
    }

    [Fact]
    public async Task Reconnecting_draining_agent_stays_excluded()
    {
        var clock = new ManualClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var hello = Hello();
        var session = await registry.RegisterAsync(hello, TestContext.Current.CancellationToken);
        await using var command = database.DataSource.CreateCommand("UPDATE agents SET status=2 WHERE id=$1");
        command.Parameters.AddWithValue(session.AgentId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        session = await registry.RegisterAsync(hello, TestContext.Current.CancellationToken);
        Assert.Equal(AgentStatus.Draining, registry.Find(session.AgentId)!.Status);
        Assert.True(await registry.HeartbeatAsync(session, Heartbeat(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(registry.DispatchableAgents(), item => item.Id == session.AgentId);
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("slots")]
    [InlineData("ram")]
    [InlineData("timestamp")]
    public async Task Invalid_heartbeat_cannot_refresh_the_lease(string kind)
    {
        var clock = new ManualClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), TestContext.Current.CancellationToken);
        var heartbeat = Heartbeat();
        if (kind == "nan") heartbeat.CpuPercent = double.NaN;
        if (kind == "slots") heartbeat.SlotsUsed = uint.MaxValue;
        if (kind == "ram") heartbeat.RamUsedMb = ulong.MaxValue;
        if (kind == "timestamp") heartbeat.At = new Timestamp { Nanos = -1 };
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.HeartbeatAsync(session, heartbeat, TestContext.Current.CancellationToken));
        Assert.Equal(DateTimeOffset.UnixEpoch, registry.Find(session.AgentId)!.LastHeartbeatAt);
    }

    [Fact]
    public async Task Actual_agent_registers_Healthy_with_real_gateway_and_database_within_five_seconds()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        await using var gateway = await StartGateway(registry);
        var address = Address(gateway);
        var id = Guid.NewGuid();
        var options = new AgentOptions([address], id.ToString(), $"worker-{id:N}", "ci", 2,
            heartbeatInterval: TimeSpan.FromMilliseconds(50));
        var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System), new NoCommands(),
            TimeProvider.System, NullLogger<AgentConnection>.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.StartNew();
        var running = connection.RunAsync(cancellation.Token);
        try
        {
            await WaitUntil(() => registry.Find(id) is { CpuPercent: >= 0, RamUsedMb: > 0 }, running);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(AgentStatus.Healthy, registry.Find(id)!.Status);
            using var restored = new AgentRegistry(database.DataSource, TimeProvider.System);
            await restored.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AgentStatus.Healthy, restored.Find(id)!.Status);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Gateway_requires_Hello_as_first_frame()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        await using var gateway = await StartGateway(registry);
        using var channel = GrpcChannel.ForAddress(Address(gateway));
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Heartbeat = Heartbeat() }, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task Native_installer_layout_loads_persistent_config_and_registers_within_five_seconds()
    {
        var executable = Environment.GetEnvironmentVariable("WF_AGENT_EXECUTABLE");
        Assert.SkipUnless(executable is not null, "Set WF_AGENT_EXECUTABLE to a published Native AOT agent.");
        Assert.True(File.Exists(executable));
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        await using var gateway = await StartGateway(registry);
        var directory = Path.Combine(Path.GetTempPath(), $"wf-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var binary = Path.Combine(directory, Path.GetFileName(executable));
        var config = Path.Combine(directory, "appsettings.json");
        var id = Guid.NewGuid();
        try
        {
            File.Copy(executable, binary);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                Agent = new { Id = id, Name = $"native-{id:N}", Engines = new[] { Address(gateway).ToString() } }
            }), TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(binary)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                await WaitUntil(() => registry.Find(id) is not null, process.WaitForExitAsync(TestContext.Current.CancellationToken));
                Assert.Equal(AgentStatus.Healthy, registry.Find(id)!.Status);
                Assert.Equal($"native-{id:N}", registry.Find(id)!.Name);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                await Task.WhenAll(stdout, stderr);
            }
        }
        finally
        {
            if (File.Exists(binary)) File.Delete(binary);
            if (File.Exists(config)) File.Delete(config);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Placement_uses_durable_registry_capacity_and_excludes_stale_or_draining_agents()
    {
        var clock = new ManualClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), TestContext.Current.CancellationToken);
        var request = new PlacementRequest(Guid.NewGuid(), "node", "task", 1, new string('a', 64), RequiredRamMb: 512);
        var placement = new PlacementEngine();
        Assert.Equal(session.AgentId, placement.Propose(request, [registry.Find(session.AgentId)!.Capacity(clock.GetUtcNow())]).Agent!.Id);
        var heartbeat = Heartbeat();
        heartbeat.RamUsedMb = 7900;
        await registry.HeartbeatAsync(session, heartbeat, TestContext.Current.CancellationToken);
        Assert.Null(placement.Propose(request, [registry.Find(session.AgentId)!.Capacity(clock.GetUtcNow())]).Agent);
        await registry.HeartbeatAsync(session, Heartbeat(), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Null(placement.Propose(request, [registry.Find(session.AgentId)!.Capacity(clock.GetUtcNow())]).Agent);
        await using var command = database.DataSource.CreateCommand("UPDATE agents SET status=2 WHERE id=$1");
        command.Parameters.AddWithValue(session.AgentId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await registry.LoadAsync(TestContext.Current.CancellationToken);
        await registry.HeartbeatAsync(session, Heartbeat(), TestContext.Current.CancellationToken);
        Assert.Null(placement.Propose(request, [registry.Find(session.AgentId)!.Capacity(clock.GetUtcNow())]).Agent);
    }

    private static async Task WaitUntil(Func<bool> condition, Task running)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (running.IsCompleted) await running;
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task<WebApplication> StartGateway(AgentRegistry registry)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        var app = builder.Build();
        app.MapGrpcService<AgentGatewayService>();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()!.Addresses.Single());

    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new Hello
        {
            Id = id.ToString(), Name = $"worker-{id:N}", Os = "linux", Arch = "x64", Cores = 4,
            RamMb = 8192, MaxTasks = 2, Pool = "ci", Version = "1.0", ProtocolVersion = 1
        };
    }

    private static Heartbeat Heartbeat() => new()
    {
        At = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch), CpuPercent = 25,
        RamUsedMb = 512, DiskFreeMb = 1000, SlotsUsed = 1
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class NoCommands : IAgentCommandHandler
    {
        public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Registration does not dispatch commands.");
    }
}

public class AgentDatabase : IAsyncLifetime
{
    protected virtual IReadOnlyList<string> Migrations => ["0005_agents.sql"];
    private PostgreSqlContainer? postgres;
    private string? schema;
    private string connectionString = "";
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var external = Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
        if (external is null)
        {
            postgres = new PostgreSqlBuilder("postgres:17-alpine")
                .WithCommand("-c", "fsync=on", "-c", "full_page_writes=on", "-c", "synchronous_commit=on").Build();
            await postgres.StartAsync(token);
            connectionString = postgres.GetConnectionString();
        }
        else
        {
            schema = $"agents_{Guid.NewGuid():N}";
            connectionString = new NpgsqlConnectionStringBuilder(external) { SearchPath = schema }.ConnectionString;
            await using var admin = new NpgsqlConnection(external);
            await admin.OpenAsync(token);
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
            await create.ExecuteNonQueryAsync(token);
        }
        DataSource = NpgsqlDataSource.Create(connectionString);
        foreach (var name in Migrations)
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", name), token);
            await using var command = DataSource.CreateCommand(migration);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
        else if (schema is not null)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
