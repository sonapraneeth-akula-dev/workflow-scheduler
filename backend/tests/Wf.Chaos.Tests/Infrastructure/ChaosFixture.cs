using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Sdk;
using static Wf.Chaos.Tests.ScheduleExactlyOnceTests;

namespace Wf.Chaos.Tests.Infrastructure;

internal sealed class ChaosFixture : IAsyncDisposable
{
    private readonly INetwork network = new NetworkBuilder().Build();
    private readonly List<ChaosAgent> agents = [];
    private readonly List<EngineProcess> engines = [];
    private readonly Dictionary<Guid, string> markers = [];
    private readonly byte[] storageKey = RandomNumberGenerator.GetBytes(32);
    private IContainer? proxy;
    private IContainer? disk;
    private IContainer? standby;
    private NpgsqlDataSource? standbySource;
    private bool promoted;
    private HttpClient? proxyApi;
    private ChaosDatabase? database;
    private string proxiedDatabase = "";
    private WorkflowDefinition definition = null!;
    private Guid version;

    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "wf-chaos-" + Guid.NewGuid().ToString("N")[..12]);
    internal string Pool { get; } = "chaos-" + Guid.NewGuid().ToString("N");
    internal IReadOnlyList<EngineProcess> Engines => engines;
    internal IReadOnlyList<ChaosAgent> Agents => agents;
    internal NpgsqlDataSource Source => promoted ? standbySource! : database!.Source;
    internal RunStore Store => new(Source, NullLogger<RunStore>.Instance);

    internal static async Task<ChaosFixture> StartAsync(CancellationToken token)
    {
        var fixture = new ChaosFixture();
        try { await fixture.StartCoreAsync(token); return fixture; }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Root);
        await network.CreateAsync(token);
        database = await ChaosDatabase.StartAsync(token, requireCommitTimestamps: false, network);
        // Host routing is captured when the proxy container is built; engine ports attach later.
        await TestcontainersSettings.ExposeHostPortsAsync(Array.Empty<ushort>(), token);
        proxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0").WithNetwork(network)
            .WithPortBinding(8474, true).WithPortBinding(15432, true)
            .WithPortBinding(19000, true).WithPortBinding(19001, true)
            .WithPortBinding(19002, true).WithPortBinding(19003, true)
            .WithCreateParameterModifier(parameters =>
            {
                var bindingsByPort = parameters.HostConfig?.PortBindings ??
                    throw new InvalidOperationException("Chaos proxy ports were not configured.");
                foreach (var bindings in bindingsByPort.Values)
                    foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8474).ForPath("/version")))
            .Build();
        await proxy.StartAsync(token);
        proxyApi = new HttpClient { BaseAddress = ProxyAddress(8474), Timeout = TimeSpan.FromSeconds(10) };
        await CreateProxy("database", 15432, "postgres:5432", token);
        proxiedDatabase = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Host = proxy.Hostname, Port = proxy.GetMappedPublicPort(15432), Timeout = 2, CommandTimeout = 3
        }.ConnectionString;
        await SeedPackageAsync(token);
        for (var index = 0; index < 2; index++)
            engines.Add(EngineProcess.Start(proxiedDatabase, Root, index, start =>
            {
                start.Environment["Storage__Root"] = Path.Combine(Root, "storage");
                start.Environment["Storage__UploadHmacKey"] = Convert.ToBase64String(storageKey);
                start.Environment["Engine__ExecuteRuns"] = "true";
            }));
        await TestcontainersSettings.ExposeHostPortsAsync(engines.Select(engine => checked((ushort)engine.Gateway.Port)), token);
        await LeaderAsync(token);
        for (var agent = 0; agent < 2; agent++)
        {
            for (var engine = 0; engine < 2; engine++)
                await CreateProxy($"agent-{agent}-engine-{engine}", 19000 + agent * 2 + engine,
                    $"host.testcontainers.internal:{engines[engine].Gateway.Port}", token);
            agents.Add(StartAgent(agent, Guid.NewGuid()));
        }
        await LeaderAsync(token);
        await UntilAsync(async () => await ScalarAsync<long>("SELECT count(*) FROM agents WHERE last_heartbeat_at>now()-interval '10 seconds'", token) == 2,
            TimeSpan.FromSeconds(30), token);
        disk = new ContainerBuilder("postgres:17-alpine").WithEntrypoint("sh", "-c").WithCommand("sleep 3600")
            .WithCreateParameterModifier(parameters => (parameters.HostConfig ??= new()).Tmpfs =
                new Dictionary<string, string> { ["/fault"] = "rw,noexec,nosuid,size=8388608" }).Build();
        await disk.StartAsync(token);
    }

    internal async Task<EngineProcess> LeaderAsync(CancellationToken token)
    {
        EngineProcess? leader = null;
        await UntilAsync(async () =>
        {
            foreach (var engine in engines.Where(engine => !engine.Killed))
                if (await engine.IsLeaderAsync(token)) { leader = engine; return true; }
            return false;
        }, TimeSpan.FromSeconds(30), token);
        return leader!;
    }

    internal Task SetDatabaseAvailableAsync(bool available, CancellationToken token) =>
        SetProxy("database", available, token);

    internal async Task SetAgentAvailableAsync(int agent, bool available, CancellationToken token)
    {
        _ = agents[agent];
        for (var engine = 0; engine < 2; engine++)
            await SetProxy($"agent-{agent}-engine-{engine}", available, token);
    }

    internal async Task ProbeDatabaseProxyAsync(CancellationToken token)
    {
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(proxiedDatabase)
            { Pooling = false, Timeout = 1, CommandTimeout = 1 }.ConnectionString);
        await using var query = source.CreateCommand("SELECT 1");
        if (!Equals(1, await query.ExecuteScalarAsync(token))) throw new InvalidDataException("Database proxy probe returned no row.");
    }

    internal async Task KillAgentAsync(int index, CancellationToken token, bool entireProcessTree = false) =>
        await agents[index].KillAsync(token, entireProcessTree);

    internal async Task RestartAgentAsync(int index, CancellationToken token)
    {
        var old = agents[index];
        if (!old.HasExited) throw new InvalidOperationException("Kill the owned agent before restarting it.");
        var id = old.Id;
        var session = await ScalarAsync<Guid>("SELECT session_id FROM agents WHERE id=$1", token, id);
        await old.DisposeAsync();
        agents[index] = StartAgent(index, id);
        await UntilAsync(async () => await ScalarAsync<Guid>("SELECT session_id FROM agents WHERE id=$1", token, id) != session,
            TimeSpan.FromSeconds(20), token);
    }

    internal async Task WaitForHeartbeatsAsync(CancellationToken token)
    {
        var first = await ScalarAsync<DateTimeOffset>("SELECT last_heartbeat_at FROM agents WHERE id=$1", token, agents[0].Id);
        var second = await ScalarAsync<DateTimeOffset>("SELECT last_heartbeat_at FROM agents WHERE id=$1", token, agents[1].Id);
        await UntilAsync(async () => await ScalarAsync<long>("""
            SELECT count(*) FROM agents WHERE status=0 AND
                ((id=$1 AND last_heartbeat_at<>$2) OR (id=$3 AND last_heartbeat_at<>$4))
            """, token, agents[0].Id, first, agents[1].Id, second) == 2, TimeSpan.FromSeconds(15), token);
    }

    internal async Task<Guid> StartRunAsync(string marker, CancellationToken token, bool padSnapshots = false)
    {
        var variables = new Dictionary<string, JsonElement>
        {
            ["checkpointMarker"] = JsonSerializer.SerializeToElement(marker),
            ["amount"] = JsonSerializer.SerializeToElement(0),
            ["chaosPadding"] = JsonSerializer.SerializeToElement(
                padSnapshots ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(64 * 1024)) : "")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var run = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata,
            message => throw new InvalidOperationException(message), variables: variables);
        await new CommandInbox(Source, TimeProvider.System).TriggerAsync(
            new(run.Snapshot, definition.Name, version, DateTimeOffset.UtcNow.AddMinutes(4)), "chaos-fixture", token);
        markers.Add(run.Snapshot.RunId, marker);
        return run.Snapshot.RunId;
    }

    internal async Task<(int Agent, int Pid, string MetadataPath, ulong StartIdentity)> HostAsync(Guid runId, CancellationToken token)
    {
        (int Agent, int Pid, string MetadataPath, ulong StartIdentity)? found = null;
        await UntilAsync(async () =>
        {
            if (!File.Exists(markers[runId])) return false;
            using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(markers[runId], token));
            for (var index = 0; index < agents.Count; index++)
            {
                var lifetimes = Path.Combine(agents[index].DirectoryPath, "results", "host-lifetimes", agents[index].Id.ToString("N"));
                if (!Directory.Exists(lifetimes)) continue;
                foreach (var path in Directory.EnumerateFiles(lifetimes, "dispatch.pb", SearchOption.AllDirectories))
                {
                    var dispatch = Wf.Contracts.Agent.V1.Dispatch.Parser.ParseFrom(await File.ReadAllBytesAsync(path, token));
                    if (dispatch.Task?.RunId != runId.ToString("D")) continue;
                    found = (index, marker.RootElement.GetProperty("pid").GetInt32(),
                        Path.Combine(Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName, "attempt.json"),
                        marker.RootElement.GetProperty("startIdentity").GetUInt64());
                    return true;
                }
            }
            return false;
        }, TimeSpan.FromSeconds(30), token);
        return found!.Value;
    }

    internal async Task KillHostAsync(Guid runId, CancellationToken token)
    {
        var host = await HostAsync(runId, token);
        StopHost(host.Pid, host.StartIdentity);
    }

    private static void StopRecordedHost(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var record = JsonDocument.Parse(input);
        var root = record.RootElement;
        StopHost(root.GetProperty("Pid").GetInt32(), root.GetProperty("StartIdentity").GetUInt64());
    }

    private static void StopHost(int pid, ulong expectedIdentity)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited) return;
            ulong identity;
            if (OperatingSystem.IsWindows()) identity = checked((ulong)process.StartTime.ToFileTimeUtc());
            else
            {
                var stat = File.ReadAllText($"/proc/{process.Id}/stat");
                identity = ulong.Parse(stat[(stat.LastIndexOf(')') + 1)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)[19], CultureInfo.InvariantCulture);
            }
            if (identity != expectedIdentity)
                throw new InvalidOperationException("Chaos fixture refuses to kill a reused task-host PID.");
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5_000)) throw new TimeoutException("Owned SDK host did not exit after fault injection.");
        }
    }

    internal async Task<string> FillDiskAsync(CancellationToken token)
    {
        var result = await disk!.ExecAsync(["sh", "-c", "LC_ALL=C dd if=/dev/zero of=/fault/fill bs=1048576 count=9"], token);
        if (result.ExitCode == 0 || !result.Stderr.Contains("No space left on device", StringComparison.Ordinal))
            throw new InvalidOperationException($"Bounded disk injector did not produce ENOSPC: {result.ExitCode}; {result.Stderr}");
        return result.Stderr;
    }

    internal async Task RecoverDiskAsync(CancellationToken token)
    {
        var result = await disk!.ExecAsync(["sh", "-c", "rm -f /fault/fill; printf recovered > /fault/probe; test \"$(cat /fault/probe)\" = recovered"], token);
        if (result.ExitCode != 0) throw new IOException($"Bounded disk did not recover: {result.Stderr}");
    }

    internal async Task PrepareStandbyAsync(CancellationToken token)
    {
        if (standby is not null) throw new InvalidOperationException("The owned standby is already configured.");
        var hba = await database!.Container.ExecAsync(["sh", "-c",
            "printf '\\nhost replication all all scram-sha-256\\n' >> \"$PGDATA/pg_hba.conf\""], token);
        if (hba.ExitCode != 0) throw new IOException($"Cannot enable owned standby replication: {hba.Stderr}");
        await ExecuteAsync("SELECT pg_reload_conf()", token);
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        standby = new ContainerBuilder("postgres:17-alpine").WithNetwork(network).WithNetworkAliases("postgres-standby")
            .WithPortBinding(5432, true).WithCreateParameterModifier(parameters => parameters.User = "postgres")
            .WithEnvironment("PGDATA", "/var/lib/postgresql/data/standby")
            .WithEnvironment("PGUSER", settings.Username!)
            .WithEnvironment("PGPASSWORD", settings.Password!)
            .WithEntrypoint("sh", "-c").WithCommand(
                """pg_basebackup --dbname='host=postgres application_name=wf_chaos_standby' --username="$PGUSER" --pgdata="$PGDATA" --write-recovery-conf --checkpoint=fast && exec postgres -D "$PGDATA" -c max_connections=200 -c track_commit_timestamp=on -c synchronous_standby_names=''""")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                "pg_isready", "-U", settings.Username!, "-d", settings.Database!)).Build();
        await standby.StartAsync(token);
        settings.Host = standby.Hostname;
        settings.Port = standby.GetMappedPublicPort(5432);
        settings.Timeout = 2;
        settings.CommandTimeout = 3;
        standbySource = NpgsqlDataSource.Create(settings.ConnectionString);
        await using (var recovery = standbySource.CreateCommand("SELECT pg_is_in_recovery()"))
            if (!Equals(true, await recovery.ExecuteScalarAsync(token)))
                throw new InvalidDataException("The owned database replica did not start as a physical standby.");
        await UntilAsync(async () => await ScalarAsync<long>("""
            SELECT count(*) FROM pg_stat_replication
            WHERE application_name='wf_chaos_standby' AND state='streaming'
            """, token) == 1, TimeSpan.FromSeconds(15), token);
        await ExecuteAsync("ALTER SYSTEM SET synchronous_standby_names='FIRST 1 (wf_chaos_standby)'", token);
        await ExecuteAsync("SELECT pg_reload_conf()", token);
        await UntilAsync(async () => await ScalarAsync<long>("""
            SELECT count(*) FROM pg_stat_replication
            WHERE application_name='wf_chaos_standby' AND sync_state='sync'
            """, token) == 1, TimeSpan.FromSeconds(15), token);
    }

    internal async Task PromoteStandbyAsync(CancellationToken token)
    {
        if (standbySource is null || promoted) throw new InvalidOperationException("Prepare an owned standby before promotion.");
        await SetDatabaseAvailableAsync(false, token);
        await database!.Container.StopAsync(token);
        await using (var promote = standbySource.CreateCommand("SELECT pg_promote(true,10)"))
        {
            promote.CommandTimeout = 15;
            if (!Equals(true, await promote.ExecuteScalarAsync(token)))
                throw new InvalidDataException("The owned standby did not become writable.");
        }
        promoted = true;
        using var response = await proxyApi!.PostAsJsonAsync("/proxies/database",
            new { upstream = "postgres-standby:5432", enabled = false }, token);
        response.EnsureSuccessStatusCode();
        await SetDatabaseAvailableAsync(true, token);
        if (await ScalarAsync<bool>("SELECT pg_is_in_recovery()", token))
            throw new InvalidDataException("The promoted database still reports standby recovery.");
    }

    internal async Task PrepareSnapshotDiskFaultAsync(Guid runId, CancellationToken token)
    {
        var directory = await database!.Container.ExecAsync(["sh", "-c",
            "mkdir /fault/tablespace && chown postgres:postgres /fault/tablespace"], token);
        if (directory.ExitCode != 0) throw new IOException($"Cannot prepare bounded database tablespace: {directory.Stderr}");
        await ExecuteAsync("CREATE TABLESPACE chaos_fault LOCATION '/fault/tablespace'", token);
        var day = await ScalarAsync<DateOnly>("SELECT run_day FROM runs WHERE id=$1", token, runId);
        await ExecuteAsync($"ALTER TABLE run_snapshots_{day.ToString("yyyyMM", CultureInfo.InvariantCulture)} SET TABLESPACE chaos_fault", token);
    }

    internal async Task FillSnapshotDiskAsync(CancellationToken token)
    {
        var result = await database!.Container.ExecAsync(
            ["sh", "-c", "LC_ALL=C dd if=/dev/zero of=/fault/fill bs=1048576 count=9"], token);
        if (result.ExitCode == 0 || !result.Stderr.Contains("No space left on device", StringComparison.Ordinal))
            throw new IOException($"Database snapshot disk did not produce ENOSPC: {result.ExitCode}; {result.Stderr}");
    }

    internal async Task RecoverSnapshotDiskAsync(CancellationToken token)
    {
        var result = await database!.Container.ExecAsync(["rm", "-f", "/fault/fill"], token);
        if (result.ExitCode != 0) throw new IOException($"Cannot recover owned snapshot disk: {result.Stderr}");
    }

    private async Task ExecuteAsync(string sql, CancellationToken token)
    {
        await using var command = Source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(token);
    }

    internal async Task<T> ScalarAsync<T>(string sql, CancellationToken token, params object[] values)
    {
        await using var query = Source.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
            throw new InvalidDataException("Chaos observation returned no row.");
        return reader.GetFieldValue<T>(0);
    }

    internal async Task UntilAsync(Func<Task<bool>> condition, TimeSpan budget, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(budget);
        try
        {
            while (!await condition())
            {
                foreach (var engine in engines.Where(engine => !engine.Killed))
                    if (engine.HasExited)
                        throw new InvalidOperationException($"Engine {engine.Id} exited. {await CaptureDiagnosticsAsync()}");
                foreach (var agent in agents.Where(agent => !agent.Killed))
                    if (agent.HasExited) throw new InvalidOperationException($"Agent {agent.Id} exited: {agent.Diagnostics}");
                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Chaos observation exceeded {budget}. {await CaptureDiagnosticsAsync()}");
        }
    }

    private async Task<string> CaptureDiagnosticsAsync()
    {
        var diagnostics = new List<string>(agents.Select(agent => $"Agent {agent.Id}: {agent.Diagnostics}"));
        diagnostics.Add(await ScalarAsync<string>("""
                SELECT json_build_object('now',clock_timestamp(),
                    'agents',(SELECT json_agg(row_to_json(a)) FROM
                        (SELECT id,session_id,last_heartbeat_at,status FROM agents) a),
                    'runs',(SELECT json_agg(row_to_json(r)) FROM
                        (SELECT id,status,status_reason,last_seq FROM runs LIMIT 10) r),
                    'attempts',(SELECT json_agg(row_to_json(t)) FROM
                        (SELECT run_id,task_key,attempt,agent_id,status,error FROM task_attempts LIMIT 20) t),
                    'lease',(SELECT json_agg(row_to_json(l)) FROM
                        (SELECT holder,epoch,expires_at FROM leases) l))::text
            """, CancellationToken.None));
        foreach (var engine in engines)
        {
            await engine.KillAsync();
            foreach (var path in new[] { engine.LogPath, engine.LogPath + ".stderr" })
            {
                var text = await File.ReadAllTextAsync(path, CancellationToken.None);
                diagnostics.Add(text.Length > 12_000 ? text[^12_000..] : text);
            }
        }
        return string.Join(Environment.NewLine, diagnostics);
    }

    private ChaosAgent StartAgent(int index, Guid id) => new(id, Path.Combine(Root, $"agent-{index}"), Pool,
        [ProxyAddress(19000 + index * 2), ProxyAddress(19001 + index * 2)]);

    private Uri ProxyAddress(int port) => new($"http://{proxy!.Hostname}:{proxy.GetMappedPublicPort(port)}");

    private async Task CreateProxy(string name, int port, string upstream, CancellationToken token)
    {
        using var response = await proxyApi!.PostAsJsonAsync("/proxies",
            new { name, listen = $"0.0.0.0:{port}", upstream, enabled = true }, token);
        response.EnsureSuccessStatusCode();
    }

    private async Task SetProxy(string name, bool available, CancellationToken token)
    {
        using var response = await proxyApi!.PostAsJsonAsync("/proxies/" + name, new { enabled = available }, token);
        response.EnsureSuccessStatusCode();
    }

    private async Task SeedPackageAsync(CancellationToken token)
    {
        await SeedAsync(Source, token, schedules: 0);
        definition = Workflow.Define("chaos/release/workflow", "1.0.0", workflow => workflow.Type("release")
            .Variables(variables => variables.String("checkpointMarker", "").Int("amount", 0).String("chaosPadding", ""))
            .RunsOn(new PlacementSelector(pool: Pool)).PostProcess(stage => stage.Task("drain", _ => { })));
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                await using var input = File.OpenRead(path);
                await using var output = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Fastest).Open();
                await input.CopyToAsync(output, token);
            }
            await using var manifest = new StreamWriter(zip.CreateEntry(PackageCache.ManifestName).Open(), Encoding.UTF8);
            await manifest.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(Wf.TaskHost.Fixture.FixtureMarker).Assembly.Location)}}"}""".AsMemory(), token);
        }
        var package = buffer.ToArray();
        var hash = SHA256.HashData(package);
        using var blobs = new FileSystemBlobStore(Path.Combine(Root, "storage"), storageKey);
        using var stream = new MemoryStream(package, writable: false);
        await blobs.UploadAsync(new(BlobKind.Packages, Convert.ToHexStringLower(hash), package.LongLength), stream, token);
        await using var update = Source.CreateCommand("""
            UPDATE workflow_versions SET manifest=$1::jsonb,package_sha256=$2,package_size=$3,
                package_uri='packages/'||encode($2,'hex')||'.zip' RETURNING id
            """);
        update.Parameters.AddWithValue(Encoding.UTF8.GetString(ManifestWriter.WriteUtf8(definition)));
        update.Parameters.AddWithValue(hash);
        update.Parameters.AddWithValue(package.LongLength);
        version = (Guid)(await update.ExecuteScalarAsync(token))!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in agents) await agent.DisposeAsync();
        foreach (var agent in agents)
        {
            var lifetimes = Path.Combine(agent.DirectoryPath, "results", "host-lifetimes", agent.Id.ToString("N"));
            if (Directory.Exists(lifetimes))
                foreach (var path in Directory.EnumerateFiles(lifetimes, "attempt.json", SearchOption.AllDirectories))
                    StopRecordedHost(path);
        }
        foreach (var engine in engines) await engine.DisposeAsync();
        proxyApi?.Dispose();
        if (disk is not null) await disk.DisposeAsync();
        if (proxy is not null) await proxy.DisposeAsync();
        if (standbySource is not null) await standbySource.DisposeAsync();
        if (standby is not null) await standby.DisposeAsync();
        if (database is not null) await database.DisposeAsync();
        await network.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class ChaosAgent : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentQueue<string> diagnostics = new();
    internal Guid Id { get; }
    internal string DirectoryPath { get; }
    internal bool Killed { get; private set; }
    internal bool HasExited => process.HasExited;
    internal string Diagnostics => string.Join(Environment.NewLine, diagnostics);

    internal ChaosAgent(Guid id, string directory, string pool, IReadOnlyList<Uri> engines)
    {
        Id = id;
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        var assembly = typeof(ChaosFixture).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(AgentConnection).Assembly.Location })
            start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["Agent__Id"] = id.ToString("D");
        start.Environment["Agent__Name"] = "chaos-" + id.ToString("N");
        start.Environment["Agent__Pool"] = pool;
        start.Environment["Agent__MaxTasks"] = "1";
        start.Environment["Agent__AllowInsecureTransport"] = "true";
        start.Environment["Agent__ResultJournalDirectory"] = Path.Combine(directory, "results");
        start.Environment["Agent__LogSpoolDirectory"] = Path.Combine(directory, "logs");
        start.Environment["Agent__PackageCache"] = Path.Combine(directory, "packages");
        start.Environment["Secrets__LocalDirectory"] = Path.Combine(directory, "secrets");
        start.Environment["Logging__LogLevel__Default"] = "Information";
        for (var index = 0; index < engines.Count; index++)
            start.Environment["Agent__Engines__" + index.ToString(CultureInfo.InvariantCulture)] = engines[index].AbsoluteUri;
        process = Process.Start(start) ?? throw new InvalidOperationException("Owned chaos agent did not start.");
        process.OutputDataReceived += (_, line) => Capture(line.Data);
        process.ErrorDataReceived += (_, line) => Capture(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void Capture(string? line)
    {
        if (line is null) return;
        diagnostics.Enqueue(line);
        while (diagnostics.Count > 100) diagnostics.TryDequeue(out _);
    }

    internal Task KillAsync(CancellationToken token, bool entireProcessTree = false)
    {
        token.ThrowIfCancellationRequested();
        Killed = true;
        if (!process.HasExited) process.Kill(entireProcessTree);
        if (!process.WaitForExit(5_000)) throw new TimeoutException("Owned chaos agent did not exit.");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync(CancellationToken.None);
        process.CancelOutputRead();
        process.CancelErrorRead();
        process.Dispose();
    }
}
