#if !ENGINE_BASELINE
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Registration;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Sdk;

namespace Wf.Benchmarks;

public static class DispatchLatencyBenchmarks
{
    public const int DispatchSamples = 10_000;
    public const int TriggerSamples = 100;
    private const int DispatchWarmups = 100;
    private const int TriggerWarmups = 10;
    private const string WorkflowId = "dispatch-bench/release/tiny";
    private const string Pool = "dispatch-bench";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static WorkflowDefinition DefineWorkflow() => Workflow.Define(WorkflowId, "1.0.0", workflow => workflow
        .Type("release")
        .Node("work", node => node.Task("task", context =>
        {
            context.Outputs["started"] = Stopwatch.GetTimestamp();
            context.Outputs["pid"] = Environment.ProcessId;
        })));

    public static bool Passes(double[] dispatchMs, double[] triggerMs) =>
        dispatchMs.Length == DispatchSamples && triggerMs.Length == TriggerSamples &&
        dispatchMs.Concat(triggerMs).All(value => double.IsFinite(value) && value > 0) &&
        Percentile(dispatchMs, 0.95) <= 50 && Percentile(dispatchMs, 0.99) <= 200 &&
        Percentile(triggerMs, 0.95) <= 1000;

    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancel;
        var directory = Path.GetFullPath(Path.Combine(".agent-artifacts", "BenchmarkDotNet.Artifacts", "dispatch-latency", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var (report, passed) = await MeasureAsync(directory, timeout.Token);
            var json = JsonSerializer.Serialize(report, Json);
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), json, timeout.Token);
            Console.WriteLine(json);
            Console.WriteLine("Report: " + directory);
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.json"),
                JsonSerializer.Serialize(new { passed = false, error = error.ToString() }, Json), CancellationToken.None);
            if (error is RegistrationValidationException validation)
                Console.Error.WriteLine(JsonSerializer.Serialize(validation.Errors, Json));
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<(object Report, bool Passed)> MeasureAsync(string directory, CancellationToken token)
    {
        PostgreSqlContainer? postgres = null;
        var external = Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
        var mode = external is null ? "isolated postgres:17-alpine container" : "WF_TEST_POSTGRES unique schema";
        var schema = "dispatch_bench_" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            if (external is null)
            {
                postgres = new PostgreSqlBuilder("postgres:17-alpine")
                    .WithCommand("-c", "fsync=on", "-c", "full_page_writes=on", "-c", "synchronous_commit=on").Build();
                await postgres.StartAsync(token);
                external = postgres.GetConnectionString();
            }
            await using (var admin = new NpgsqlConnection(external))
            {
                await admin.OpenAsync(token);
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
                await create.ExecuteNonQueryAsync(token);
                created = true;
            }
            var connectionString = new NpgsqlConnectionStringBuilder(external) { SearchPath = schema }.ConnectionString;
            Migrator.Upgrade(connectionString, NullLogger.Instance);
            await using var source = NpgsqlDataSource.Create(connectionString);
            return await MeasureOnDatabaseAsync(source, directory, mode, token);
        }
        finally
        {
            try
            {
                if (created)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(external!)
                        { Pooling = false, Timeout = 10, CommandTimeout = 15 }.ConnectionString);
                    await admin.OpenAsync(cleanup.Token);
                    await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
                    await drop.ExecuteNonQueryAsync(cleanup.Token);
                }
            }
            finally
            {
                if (postgres is not null) await postgres.DisposeAsync();
            }
        }
    }

    private static async Task<(object Report, bool Passed)> MeasureOnDatabaseAsync(
        NpgsqlDataSource source, string directory, string databaseMode, CancellationToken token)
    {
        var durability = new Dictionary<string, string>();
        string postgresVersion;
        await using (var query = source.CreateCommand("""
            SELECT current_setting('fsync'),current_setting('full_page_writes'),current_setting('synchronous_commit'),version()
            """))
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("PostgreSQL settings are missing.");
            durability["fsync"] = reader.GetString(0);
            durability["full_page_writes"] = reader.GetString(1);
            durability["synchronous_commit"] = reader.GetString(2);
            postgresVersion = reader.GetString(3);
        }
        if (durability.Values.Any(value => value != "on"))
            throw new InvalidOperationException("Dispatch benchmark requires PostgreSQL durability settings ON.");
        using var logging = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var root = Path.Combine(directory, "runtime");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development, Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration["Authentication:Mode"] = "Development";
            builder.Configuration["Storage:Root"] = Path.Combine(root, "blobs");
            builder.Configuration["Storage:UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(source);
            builder.Services.AddTeamAuthorization();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddWorkflowRegistrations(builder.Configuration);
            builder.Services.AddSingleton<RunStore>();
            builder.Services.AddSingleton<CommandInbox>();
            builder.Services.AddSingleton<ActiveVersionCache>();
            builder.Services.AddHostedService<ActiveVersionCache>(services => services.GetRequiredService<ActiveVersionCache>());
            await using var api = builder.Build();
            api.UseAuthentication();
            api.UseAuthorization();
            api.MapRunsEndpoints();
            api.MapPackageDownloads();
            var workflow = DefineWorkflow();
            var (version, content) = await RegisterAsync(api.Services, workflow, token);
            await api.StartAsync(token);
            var versions = api.Services.GetRequiredService<ActiveVersionCache>();
            while (!versions.IsReady) await Task.Delay(10, token);
            if (versions.GetActive(WorkflowId).VersionId != version)
                throw new InvalidDataException("Active-version cache did not load the registered package.");
            var address = Address(api);
            using var leadership = new LeaseManager(source, new("dispatch-benchmark", address), logging.CreateLogger<LeaseManager>());
            await leadership.StartAsync(token);
            using var registry = new AgentRegistry(source, TimeProvider.System, leadership);
            var options = new RunExecutionOptions(address);
            var store = new RunStore(source, NullLogger<RunStore>.Instance, leadership, TimeProvider.System, options);
            using var dispatcher = new Dispatcher(source, store, registry, new PlacementEngine(), TimeProvider.System);
            var gatewayBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            gatewayBuilder.Logging.ClearProviders();
            gatewayBuilder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
            gatewayBuilder.Services.AddGrpc();
            gatewayBuilder.Services.AddSingleton(registry);
            gatewayBuilder.Services.AddSingleton(store);
            gatewayBuilder.Services.AddSingleton(leadership);
            await using var gateway = gatewayBuilder.Build();
            gateway.MapGrpcService<AgentGatewayService>();
            await gateway.StartAsync(token);
            var id = Guid.NewGuid();
            var agentOptions = new AgentOptions([Address(gateway)], id.ToString("D"), "dispatch-" + id.ToString("N"), Pool, 2,
                heartbeatInterval: TimeSpan.FromSeconds(1), resultJournalDirectory: Path.Combine(root, "results"));
            using var cache = new PackageCache(Path.Combine(root, "cache"), NullLogger<PackageCache>.Instance);
            await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
                containmentDirectory: Path.Combine(root, "host-lifetimes"));
            var outbox = new AgentOutbox();
            await using var commands = new TaskCommands(agentOptions, cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);
            var observer = new ReceiptObserver(commands, outbox);
            using var connection = new AgentConnection(agentOptions, new AgentTelemetry(TimeProvider.System, commands),
                observer, TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            var connected = connection.RunAsync(stopping.Token);
            try
            {
                await WaitAsync(() => Task.FromResult(registry.Find(id) is not null), connected, commands, token);
                var grants = api.Services.GetRequiredKeyedService<BlobUploadGrants>(PackageDownloadEndpoints.Grants);
                var packageUri = new Uri(address, "/api/v1/packages/" + grants.Create(content, content.SizeBytes, TimeSpan.FromMinutes(15)));
                using var package = await cache.GetAsync("1.0.0", packageUri, content.Sha256, token);
                var warmPids = new HashSet<int>();
                using var recovery = new RunRecovery(source, store, leadership, TimeProvider.System, logging.CreateLogger<RunRecovery>());
                using var executor = new RunExecutionService(source, recovery, dispatcher, new(source, store, TimeProvider.System),
                    TimeProvider.System, options, grants, logging.CreateLogger<RunExecutionService>());
                await recovery.StartAsync(token);
                await executor.StartAsync(token);
                var triggers = new List<TriggerSample>(TriggerSamples);
                try
                {
                    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
                    for (var index = -TriggerWarmups; index < TriggerSamples; index++)
                    {
                        await hosts.PrewarmAsync(package, token);
                        var ready = hosts.WarmProcessIds.ToHashSet();
                        if (ready.Count != 1) throw new InvalidDataException("One-shot SDK host was not prewarmed.");
                        warmPids.UnionWith(ready);
                        var started = Stopwatch.GetTimestamp();
                        using var response = await client.PostAsJsonAsync("/api/v1/runs", new TriggerRunRequest(WorkflowId),
                            RunJsonContext.Default.TriggerRunRequest, token);
                        if (response.StatusCode != HttpStatusCode.Accepted)
                            throw new InvalidOperationException($"Trigger returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(token)}");
                        var accepted = await response.Content.ReadFromJsonAsync(RunJsonContext.Default.TriggerRunResponse, token)
                            ?? throw new InvalidDataException("Trigger response is missing.");
                        if (accepted.RunId.Version != 7) throw new InvalidDataException("Trigger did not return UUIDv7.");
                        var output = await ReadOutputAsync(source, accepted.RunId, ready, connected, commands, token);
                        var sample = new TriggerSample(accepted.RunId, Stopwatch.GetElapsedTime(started, output.Started).TotalMilliseconds, output.Pid);
                        if (index >= 0) triggers.Add(sample);
                        await WaitAsync(async () => (await store.LoadAsync(accepted.RunId, token)).Snapshot.Status == RunStatus.Succeeded,
                            connected, commands, token);
                    }
                }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await executor.StopAsync(cleanup.Token);
                    await recovery.StopAsync(cleanup.Token);
                }
                if (hosts.ProcessStartCount != TriggerSamples + TriggerWarmups)
                    throw new InvalidDataException("Trigger workload started an unmeasured cold host or reused a one-shot process.");
                var dispatch = new List<DispatchSample>(DispatchSamples);
                var runs = new RunState[DispatchSamples + DispatchWarmups];
                var prepared = 0;
                foreach (var batch in Enumerable.Range(0, runs.Length).Chunk(32))
                {
                    await Parallel.ForEachAsync(batch, new ParallelOptions
                    { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (index, preparing) =>
                    {
                        var run = new RunState(Guid.CreateVersion7(), workflow.Nodes, workflow.Metadata,
                            message => throw new InvalidDataException(message));
                        await store.CreateAsync(run, WorkflowId, version, DateTimeOffset.UtcNow.AddHours(1), preparing);
                        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, preparing);
                        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                            await store.AppendAsync(run, EventEntity.Node, "work", (int)state, DateTimeOffset.UtcNow, preparing);
                        await store.AppendAsync(run, EventEntity.Task, "work/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, preparing);
                        runs[index] = run;
                        var count = Interlocked.Increment(ref prepared);
                        if (count % 1000 == 0) Console.WriteLine($"Committed Ready fixtures: {count}/{runs.Length}");
                    });
                    await Task.Delay(10, token);
                }
                observer.Probing = true;
                for (var index = -DispatchWarmups; index < DispatchSamples; index++)
                {
                    var run = runs[index + DispatchWarmups];
                    var runId = run.Snapshot.RunId;
                    var receipt = observer.Expect(runId);
                    var dispatchUri = new Uri(address, "/api/v1/packages/" + grants.Create(content, content.SizeBytes, TimeSpan.FromMinutes(5)));
                    var started = Stopwatch.GetTimestamp();
                    var decision = await dispatcher.DispatchAsync(run, new(runId, "work", "task", 1, content.Sha256), new()
                    {
                        Task = new() { RunId = runId.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
                        Version = "1.0.0", Sha256 = content.Sha256, PackageUri = dispatchUri.AbsoluteUri,
                        DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1))
                    }, token);
                    var dispatchCallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (decision.Agent?.Id != id) throw new InvalidDataException("Dispatch did not bind the real benchmark agent.");
                    var received = await receipt.WaitAsync(TimeSpan.FromSeconds(10), token);
                    await WaitAsync(async () =>
                    {
                        var state = (await store.LoadAsync(runId, token)).Snapshot.Tasks["work/task"];
                        if (state is not (ExecutionStatus.Dispatched or ExecutionStatus.Running or ExecutionStatus.Succeeded))
                            throw new InvalidDataException($"Protocol probe ended in unexpected state {state}.");
                        return state == ExecutionStatus.Succeeded;
                    }, connected, commands, token);
                    var sample = new DispatchSample(runId, Stopwatch.GetElapsedTime(started, received).TotalMilliseconds, dispatchCallMs);
                    if (index >= 0) dispatch.Add(sample);
                    if (index >= 0 && (index + 1) % 1000 == 0)
                        Console.WriteLine($"Dispatch samples: {index + 1}/{DispatchSamples}; p95={Percentile(dispatch.Select(item => item.LatencyMs).ToArray(), 0.95):F3} ms; dispatcher call p95={Percentile(dispatch.Select(item => item.DispatchCallMs).ToArray(), 0.95):F3} ms");
                }
                if (hosts.ProcessStartCount != TriggerSamples + TriggerWarmups)
                    throw new InvalidDataException("The isolated dispatch protocol probe unexpectedly launched a task host.");
                var expected = DispatchWarmups + DispatchSamples + TriggerWarmups + TriggerSamples;
                var dispatchMs = dispatch.Select(sample => sample.LatencyMs).ToArray();
                var triggerMs = triggers.Select(sample => sample.LatencyMs).ToArray();
                var passed = Passes(dispatchMs, triggerMs);
                await File.WriteAllTextAsync(Path.Combine(directory, "dispatch-samples.json"), JsonSerializer.Serialize(dispatch, Json), token);
                await File.WriteAllTextAsync(Path.Combine(directory, "trigger-samples.json"), JsonSerializer.Serialize(triggers, Json), token);
                await VerifyAsync(source, id, version, expected, token);
                return (new
                {
                    benchmark = nameof(DispatchLatencyBenchmarks), passed,
                    environment = new
                    {
                        os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
                        architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
                        stopwatchFrequency = Stopwatch.Frequency, postgres = postgresVersion, databaseMode
                    },
                    durability, dispatchWarmups = DispatchWarmups, triggerWarmups = TriggerWarmups,
                    dispatch = Summary(dispatchMs), dispatcherCall = Summary(dispatch.Select(sample => sample.DispatchCallMs).ToArray()),
                    triggerToSdkDelegate = Summary(triggerMs),
                    measurement = "Sequential durable deliveries. Dispatch: immediately before Dispatcher.DispatchAsync through " +
                        "lease-fenced synchronous commit/outbox/Kestrel HTTP2/AgentConnection to command-handler receipt, " +
                        "excluding earlier Ready commit. Dispatch-only handler sends synthetic protocol start/completion replies " +
                        "after receipt to drain the isolated outbox; those 10000 deliveries are NOT SDK execution samples. " +
                        "Their fixture workflows remain Running; only the real SDK workflows are driven to terminal success. " +
                        "Trigger: before real Development HTTP POST through durable command inbox/100ms recovery/25ms executor/" +
                        "gRPC/prewarmed one-shot host IPC to the actual SDK delegate Stopwatch timestamp (same machine clock). " +
                        "Completion polling and per-trigger prewarming are outside the intervals. No retries or discarded measured samples.",
                    gate = "Exactly 10000 dispatches: p95 <= 50 ms, p99 <= 200 ms; exactly 100 triggers: p95 <= 1000 ms; all finite/positive.",
                    warmHostPids = warmPids, processStarts = hosts.ProcessStartCount,
                    persistedRunsAndAttempts = expected,
                    realSdkExecutions = TriggerSamples + TriggerWarmups,
                    protocolOnlyDeliveries = DispatchSamples + DispatchWarmups
                }, passed);
            }
            finally
            {
                await stopping.CancelAsync();
                await connected.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await leadership.StopAsync(cleanup.Token);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task<(Guid Version, BlobContent Content)> RegisterAsync(IServiceProvider services, WorkflowDefinition workflow, CancellationToken token)
    {
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                await using var file = File.OpenRead(path);
                await using var entry = zip.CreateEntry(Path.GetFileName(path)).Open();
                await file.CopyToAsync(entry, token);
            }
            await using var manifest = zip.CreateEntry(PackageCache.ManifestName).Open();
            await JsonSerializer.SerializeAsync(manifest, new { version = "1.0.0",
                entryAssembly = Path.GetFileName(typeof(DispatchLatencyBenchmarks).Assembly.Location) }, cancellationToken: token);
        }
        var bytes = package.ToArray();
        var content = new BlobContent(BlobKind.Packages, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        package.Position = 0;
        await services.GetRequiredService<IBlobStore>().UploadAsync(content, package, token);
        using var manifestDocument = JsonDocument.Parse(ManifestWriter.WriteUtf8(workflow));
        var registry = new TeamRegistry("dispatch-bench", [new("dispatch-bench", "release", "Dispatch benchmark", new([]))]);
        var response = await services.GetRequiredService<RegistrationService>().RegisterAsync(
            new RegistrationRequest(new string('a', 40), "Dispatch benchmark", WorkflowId, manifestDocument.RootElement,
                new(content.Sha256, content.SizeBytes), "// real warm-host benchmark workflow",
                JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry), "Benchmark setup"),
            "00000000-0000-0000-0000-000000000001", token);
        return (response.WorkflowVersion ?? throw new InvalidDataException("Workflow registration failed."), content);
    }

    private static async Task<Output> ReadOutputAsync(NpgsqlDataSource source, Guid runId, HashSet<int> warmPids,
        Task connected, TaskCommands commands, CancellationToken token)
    {
        Output? output = null;
        await WaitAsync(async () =>
        {
            await using var query = source.CreateCommand("""
                SELECT a.status,a.error,n.outputs::text FROM task_attempts a
                JOIN node_runs n ON n.run_day=a.run_day AND n.run_id=a.run_id AND n.node_key='work'
                WHERE a.run_day=uuid_v7_run_day($1) AND a.run_id=$1 AND a.task_key='work/task' AND a.attempt=1
                """);
            query.Parameters.AddWithValue(runId);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return false;
            var state = (ExecutionStatus)reader.GetInt16(0);
            if (state is ExecutionStatus.Failed or ExecutionStatus.Cancelled or ExecutionStatus.TimedOut)
                throw new InvalidDataException($"Real SDK task {runId} failed: {reader.GetValue(1)}");
            if (state != ExecutionStatus.Succeeded) return false;
            using var document = JsonDocument.Parse(reader.GetString(2));
            output = new(document.RootElement.GetProperty("started").GetInt64(), document.RootElement.GetProperty("pid").GetInt32());
            if (output.Started <= 0 || !warmPids.Contains(output.Pid))
                throw new InvalidDataException($"SDK delegate did not execute in a prewarmed process: started={output.Started}, pid={output.Pid}, warm={string.Join(',', warmPids)}.");
            return true;
        }, connected, commands, token);
        return output ?? throw new InvalidDataException("SDK output is missing.");
    }

    private static async Task VerifyAsync(NpgsqlDataSource source, Guid agent, Guid version, int count, CancellationToken token)
    {
        await using var query = source.CreateCommand("""
            SELECT
              (SELECT count(*) FROM runs),
              (SELECT count(*) FROM runs WHERE status=$3 AND version_id=$1),
              (SELECT count(*) FROM task_attempts),
              (SELECT count(*) FROM task_attempts WHERE status=$4 AND agent_id=$2 AND attempt=1 AND started_at IS NOT NULL AND ended_at IS NOT NULL),
              (SELECT count(*) FROM dispatch_outbox),
              (SELECT count(*) FROM commands WHERE kind=0 AND processed_at IS NOT NULL),
              (SELECT count(*) FROM runs WHERE status=$5 AND version_id=$1)
            """);
        query.Parameters.AddWithValue(version);
        query.Parameters.AddWithValue(agent);
        query.Parameters.AddWithValue((short)RunStatus.Succeeded);
        query.Parameters.AddWithValue((short)ExecutionStatus.Succeeded);
        query.Parameters.AddWithValue((short)RunStatus.Running);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetInt64(0) != count || reader.GetInt64(1) != TriggerSamples + TriggerWarmups ||
            reader.GetInt64(2) != count || reader.GetInt64(3) != count || reader.GetInt64(4) != 0 ||
            reader.GetInt64(5) != TriggerSamples + TriggerWarmups || reader.GetInt64(6) != DispatchSamples + DispatchWarmups)
            throw new InvalidDataException("Durable run/version/agent/attempt/outbox/command counts do not match every real execution.");
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, Task connected, TaskCommands commands, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            if (connected.IsCompleted) { await connected; throw new InvalidOperationException("Agent connection stopped."); }
            if (commands.Failure.IsCompleted) await commands.Failure;
            await Task.Delay(2, timeout.Token);
        }
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static double Percentile(double[] samples, double quantile) => samples.Order().ElementAt((int)Math.Ceiling(samples.Length * quantile) - 1);
    private static object Summary(double[] samples) => new
        { samples = samples.Length, p50Ms = Percentile(samples, 0.5), p95Ms = Percentile(samples, 0.95), p99Ms = Percentile(samples, 0.99), maxMs = samples.Max() };
    private sealed record DispatchSample(Guid RunId, double LatencyMs, double DispatchCallMs);
    private sealed record TriggerSample(Guid RunId, double LatencyMs, int Pid);
    private sealed record Output(long Started, int Pid);

    private sealed class ReceiptObserver(TaskCommands commands, AgentOutbox outbox) : IAgentCommandHandler
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<long>> pending = new();
        public bool Probing { get; set; }
        public Task<long> Expect(Guid runId)
        {
            var receipt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(runId, receipt)) throw new InvalidOperationException("Duplicate expected dispatch.");
            return receipt.Task;
        }

        public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken)
        {
            var received = Stopwatch.GetTimestamp();
            if (message.Dispatch is { } dispatch && Probing)
            {
                if (!pending.TryRemove(Guid.Parse(dispatch.Task.RunId), out var receipt))
                    throw new InvalidDataException("Unexpected or duplicate protocol-only dispatch.");
                receipt.SetResult(received);
                outbox.Enqueue(new() { TaskStarted = new() { Task = dispatch.Task.Clone(),
                    At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), ReservationEpoch = dispatch.ReservationEpoch } });
                outbox.Enqueue(new() { TaskCompleted = new() { Task = dispatch.Task.Clone(),
                    At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), ReservationEpoch = dispatch.ReservationEpoch,
                    Outcome = TaskOutcome.Succeeded } });
                return ValueTask.CompletedTask;
            }
            return commands.HandleAsync(message, cancellationToken);
        }
    }
}
#endif
