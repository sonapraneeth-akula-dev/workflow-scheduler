#if !ENGINE_BASELINE
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Registry;
using Wf.Testing.RunQueries;

namespace Wf.Benchmarks;

public sealed record FanoutLatency(int Samples, double P50Ms, double P95Ms, double P99Ms, double MaxMs);
public sealed record FanoutSqlProfile(string Query, long Calls, double ExecutionMs, long Rows);
public sealed record LiveFanoutReport(int Clients, int Runs, int EventsPerSecond, int Events, int PersistedEvents,
    int RunDeliveries, int ListCoveredUpdates, double ProducerDurationSeconds, double ProducerLagP99Ms,
    FanoutLatency RunLatency, FanoutLatency ListLatency, Dictionary<string, string> Durability, string Environment)
{
    public FanoutLatency? PersistenceLatency { get; init; }
    public FanoutSqlProfile[] DatabaseProfile { get; init; } = [];
    public double CommitDrainSeconds { get; init; }
    public bool Passed => LiveFanoutBench.Passes(this);
}

public static class LiveFanoutBench
{
    public const int Clients = 500;
    public const int Runs = 100;
    public const int Events = 50_000;
    private const int BatchesPerSecond = 100;
    private const string Type = "live-bench/release";
    private const string Workflow = Type + "/workflow";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool Passes(LiveFanoutReport report) =>
        report.Clients == Clients && report.Runs == Runs && report.EventsPerSecond == 10_000 &&
        report.Events == Events && report.PersistedEvents == Events &&
        report.RunDeliveries == Events * (Clients / Runs) && report.ListCoveredUpdates == Events * Clients &&
        report.RunLatency.Samples == report.RunDeliveries && report.ListLatency.Samples == report.ListCoveredUpdates &&
        double.IsFinite(report.ProducerDurationSeconds) && report.ProducerDurationSeconds is >= 5 and <= 5.1 &&
        double.IsFinite(report.ProducerLagP99Ms) && report.ProducerLagP99Ms is >= 0 and <= 100 &&
        LatencyPasses(report.RunLatency) && LatencyPasses(report.ListLatency) &&
        report.Durability.Count == 3 && new[] { "fsync", "full_page_writes", "synchronous_commit" }
            .All(key => report.Durability.GetValueOrDefault(key) == "on");

    private static bool LatencyPasses(FanoutLatency latency) =>
        latency.Samples > 0 && new[] { latency.P50Ms, latency.P95Ms, latency.P99Ms, latency.MaxMs }
            .All(value => double.IsFinite(value) && value > 0) &&
        latency.P50Ms <= latency.P95Ms && latency.P95Ms <= latency.P99Ms && latency.P99Ms <= latency.MaxMs &&
        latency.P95Ms <= 1000 && latency.P99Ms <= 2000;

    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancel;
        var directory = Path.GetFullPath(Path.Combine("BenchmarkDotNet.Artifacts", "live-fanout", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var report = await MeasureAsync(smoke: false, timeout.Token);
            var json = JsonSerializer.Serialize(report, Json);
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), json, timeout.Token);
            Console.WriteLine(json);
            Console.WriteLine("Report: " + directory);
            return report.Passed ? 0 : 1;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.json"),
                JsonSerializer.Serialize(new { passed = false, error = error.ToString() }, Json), CancellationToken.None);
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    public static async Task<LiveFanoutReport> MeasureAsync(bool smoke, CancellationToken token)
    {
        PostgreSqlContainer? postgres = null;
        var external = System.Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
        var schema = "live_fanout_" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            if (external is null)
            {
                postgres = new PostgreSqlBuilder("postgres:17-alpine")
                    .WithCommand("-c", "fsync=on", "-c", "full_page_writes=on", "-c", "synchronous_commit=on",
                        "-c", "shared_preload_libraries=pg_stat_statements").Build();
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
            var connection = new NpgsqlConnectionStringBuilder(external)
            {
                SearchPath = schema, MaxPoolSize = 8, MaxAutoPrepare = 64, AutoPrepareMinUsages = 2
            }.ConnectionString;
            Migrator.Upgrade(connection, NullLogger.Instance);
            await using var source = NpgsqlDataSource.Create(connection);
            if (postgres is not null)
            {
                await using var profile = source.CreateCommand("CREATE EXTENSION pg_stat_statements");
                await profile.ExecuteNonQueryAsync(token);
            }
            return await MeasureOnDatabaseAsync(source, smoke, postgres is not null, token);
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
            finally { if (postgres is not null) await postgres.DisposeAsync(); }
        }
    }

    private static async Task<LiveFanoutReport> MeasureOnDatabaseAsync(NpgsqlDataSource source, bool smoke, bool profile, CancellationToken token)
    {
        var clientCount = smoke ? 4 : Clients;
        var runCount = smoke ? 2 : Runs;
        var batches = smoke ? 20 : Events / Runs;
        var durability = new Dictionary<string, string>();
        await using (var query = source.CreateCommand("""
            SELECT current_setting('fsync'),current_setting('full_page_writes'),current_setting('synchronous_commit')
            """))
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("Missing PostgreSQL durability settings.");
            var keys = new[] { "fsync", "full_page_writes", "synchronous_commit" };
            for (var index = 0; index < keys.Length; index++) durability.Add(keys[index], reader.GetString(index));
        }
        if (durability.Values.Any(value => value != "on"))
            throw new InvalidOperationException("Live fanout acceptance requires full PostgreSQL synchronous durability.");
        var group = Guid.NewGuid();
        await using (var connection = await source.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            await new RegistryWriter().UpsertAsync(connection, transaction,
                new("live-bench", [new("live-bench", "release", "Live fanout benchmark", new([]))],
                    RoleBindings: [new(RegistryRole.Viewer, group)]), token);
            await transaction.CommitAsync(token);
        }
        var runAt = DateTimeOffset.UtcNow;
        var runDay = DateOnly.FromDateTime(runAt.UtcDateTime);
        var runs = Enumerable.Range(0, runCount).Select(_ => Guid.CreateVersion7(runAt)).ToArray();
        var version = Guid.CreateVersion7();
        foreach (var run in runs)
        {
            await using var insert = source.CreateCommand("""
                INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at)
                VALUES(uuid_v7_run_day($1),$1,$2,$3,0,'live-benchmark','{}','{}',1,now())
                """);
            insert.Parameters.AddWithValue(run);
            insert.Parameters.AddWithValue(Workflow);
            insert.Parameters.AddWithValue(version);
            await insert.ExecuteNonQueryAsync(token);
        }
        await using (var analyze = source.CreateCommand("ANALYZE runs"))
            await analyze.ExecuteNonQueryAsync(token);
        var failures = new ConcurrentQueue<string>();
        var clients = new Client?[clientCount];
        var runIndexes = runs.Select((run, index) => (run, index)).ToDictionary(pair => pair.run.ToString("D"), pair => pair.index);
        var created = Enumerable.Range(0, runCount).Select(_ => new long[batches]).ToArray();
        await using var server = await RunQueryServer.Start(source, token, live: true);
        try
        {
            var tokens = Enumerable.Range(0, clientCount).Select(_ => server.Jwt(group)).ToArray();
            await Parallel.ForEachAsync(Enumerable.Range(0, clientCount),
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = token }, async (index, cancellation) =>
                {
                    clients[index] = await Client.ConnectAsync(server.Origin, tokens[index], index % runCount, runIndexes, version,
                        batches + 1, failures, cancellation);
                });
            var connected = clients.Select(client => client ?? throw new InvalidOperationException("Missing connected client.")).ToArray();
            if ((await connected[0].Connection.InvokeAsync<LiveSubscriptionProblem?>(
                "Subscribe", "type", "unbound/release", token))?.Status != 403)
                throw new InvalidDataException("Benchmark must preserve denied cross-scope subscriptions.");
            await CommitAsync(Task.FromResult(0d), source, runDay, runs.Select(run => Change(run, 1)).ToArray(), token);
            await WaitForClientsAsync(connected, 1, failures, token);
            var roundsPerCommit = smoke ? 1 : 10;
            var runGroups = runs.Chunk(smoke ? runCount : runCount / 4).ToArray();
            var writes = new List<Task<double>>(batches / roundsPerCommit * runGroups.Length);
            var lag = new double[batches / roundsPerCommit];
            var previous = runGroups.Select(_ => Task.FromResult(0d)).ToArray();
            var origin = Stopwatch.GetTimestamp();
            double producerDuration;
            try
            {
                for (var batch = 0; batch < batches; batch += roundsPerCommit)
                {
                    var scheduled = origin + (long)((double)batch * Stopwatch.Frequency / BatchesPerSecond);
                    await WaitUntilAsync(scheduled, token);
                    var started = Stopwatch.GetTimestamp();
                    lag[batch / roundsPerCommit] = Stopwatch.GetElapsedTime(scheduled, started).TotalMilliseconds;
                    for (var run = 0; run < runCount; run++)
                    for (var round = batch; round < batch + roundsPerCommit; round++)
                        Volatile.Write(ref created[run][round], started);
                    // The producer never waits for an earlier commit or any client acknowledgement.
                    for (var lane = 0; lane < runGroups.Length; lane++)
                    {
                        previous[lane] = CommitAsync(previous[lane], source, runDay,
                            Enumerable.Range(batch, roundsPerCommit).SelectMany(round => runGroups[lane].Select(run => Change(run, round + 2))).ToArray(), token);
                        writes.Add(previous[lane]);
                    }
                }
                await WaitUntilAsync(origin + (long)((double)batches * Stopwatch.Frequency / BatchesPerSecond), token);
                producerDuration = Stopwatch.GetElapsedTime(origin).TotalSeconds;
            }
            finally { await Task.WhenAll(writes); }
            var drain = Stopwatch.GetElapsedTime(origin).TotalSeconds - producerDuration;
            var persistence = writes.Select(write => write.Result).Order().ToArray();
            await WaitForClientsAsync(connected, batches + 1, failures, token);
            var runLatency = Summarize(connected, created, lists: false, batches * clientCount);
            var listLatency = Summarize(connected, created, lists: true, batches * runCount * clientCount);
            await using var verify = source.CreateCommand("""
                SELECT count(*),count(DISTINCT(run_id,seq)),min(seq),max(seq) FROM run_events
                WHERE seq>1
                """);
            await using var persisted = await verify.ExecuteReaderAsync(token);
            if (!await persisted.ReadAsync(token) || persisted.GetInt64(0) != batches * runCount ||
                persisted.GetInt64(1) != batches * runCount || persisted.GetInt64(2) != 2 || persisted.GetInt64(3) != batches + 1)
                throw new InvalidDataException("Exact durable event count, uniqueness or sequence bounds failed.");
            Array.Sort(lag);
            var statements = new List<FanoutSqlProfile>();
            if (profile)
            {
                await using var query = source.CreateCommand("""
                    SELECT left(query,240),calls,total_exec_time,rows FROM pg_stat_statements
                    WHERE query NOT LIKE '%pg_stat_statements%' ORDER BY total_exec_time DESC LIMIT 10
                    """);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    statements.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3)));
            }
            return new(clientCount, runCount, runCount * BatchesPerSecond, batches * runCount, checked((int)persisted.GetInt64(0)),
                runLatency.Samples, listLatency.Samples, producerDuration, Percentile(lag, 0.99), runLatency, listLatency, durability,
                $"{RuntimeInformation.OSDescription}|{RuntimeInformation.FrameworkDescription}|{RuntimeInformation.ProcessArchitecture}|" +
                $"processors={System.Environment.ProcessorCount}|serverGc={GCSettings.IsServerGC}|databasePool=8|autoPrepare=64/2|signed Viewer JWT; Kestrel WebSocket MessagePack; " +
                $"each client subscribes to the type and one of the evenly sharded runs; {runGroups.Length} independent per-run-ordered synchronous synthetic COPY input lanes, actual production PostgreSQL notification/LiveFanout; not engine ingestion throughput")
            {
                CommitDrainSeconds = drain,
                DatabaseProfile = statements.ToArray(),
                PersistenceLatency = new(persistence.Length, Percentile(persistence, 0.50), Percentile(persistence, 0.95),
                    Percentile(persistence, 0.99), persistence[^1])
            };
        }
        finally
        {
            await Task.WhenAll(clients.Where(client => client is not null).Select(client => client!.Connection.DisposeAsync().AsTask()));
        }
    }

    private static RunEvent Change(Guid run, int sequence) => new(run, sequence, DateTimeOffset.UtcNow, EventEntity.Task,
        "node/task", State(sequence));

    private static async Task<double> CommitAsync(Task<double> previous, NpgsqlDataSource source, DateOnly day, RunEvent[] events,
        CancellationToken token)
    {
        await previous;
        var started = Stopwatch.GetTimestamp();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY run_events(run_day,run_id,seq,ts,kind,entity_key,payload) FROM STDIN (FORMAT BINARY)
            """, token))
        {
            foreach (var change in events)
            {
                await copy.StartRowAsync(token);
                await copy.WriteAsync(day, NpgsqlDbType.Date, token);
                await copy.WriteAsync(change.RunId, NpgsqlDbType.Uuid, token);
                await copy.WriteAsync(change.Sequence, NpgsqlDbType.Bigint, token);
                await copy.WriteAsync(change.At, NpgsqlDbType.TimestampTz, token);
                await copy.WriteAsync((short)change.Entity, NpgsqlDbType.Smallint, token);
                await copy.WriteAsync(change.Key!, NpgsqlDbType.Text, token);
                await copy.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(change), NpgsqlDbType.Bytea, token);
            }
            if (await copy.CompleteAsync(token) != (ulong)events.Length)
                throw new InvalidDataException("Synthetic durable source dropped input events.");
        }
        var runs = events.Select(change => change.RunId).Distinct().ToArray();
        await using (var update = new NpgsqlCommand("""
            UPDATE runs SET last_seq=$2 WHERE id=ANY($1) AND run_day=uuid_v7_run_day(id) AND last_seq=$3 AND status=1
            """, connection))
        {
            update.Parameters.AddWithValue(runs);
            update.Parameters.AddWithValue(events.Max(change => change.Sequence));
            update.Parameters.AddWithValue(events.Min(change => change.Sequence) - 1);
            if (await update.ExecuteNonQueryAsync(token) != runs.Length)
                throw new InvalidDataException("Synthetic source lost a run or advanced a noncontiguous sequence.");
        }
        await transaction.CommitAsync(token);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static int State(int sequence) => (int)(sequence == 1 ? ExecutionStatus.Ready : ((sequence - 2) % 3) switch
        {
            0 => ExecutionStatus.Dispatched, 1 => ExecutionStatus.Running, _ => ExecutionStatus.Retrying
        });

    private static async Task WaitUntilAsync(long scheduled, CancellationToken token)
    {
        while (scheduled > Stopwatch.GetTimestamp())
        {
            token.ThrowIfCancellationRequested();
            var remaining = Math.Max(0, (double)(scheduled - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
            await Task.Delay(TimeSpan.FromSeconds(remaining), token);
        }
    }

    private static async Task WaitForClientsAsync(Client[] clients, int sequence, ConcurrentQueue<string> failures, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!clients.All(client => client.CaughtUp(sequence)))
        {
            if (failures.TryPeek(out var failure)) throw new InvalidDataException(failure);
            await Task.Delay(10, timeout.Token);
        }
        if (failures.TryPeek(out var last)) throw new InvalidDataException(last);
    }

    private static FanoutLatency Summarize(Client[] clients, long[][] created, bool lists, int expected)
    {
        var samples = new double[expected];
        var index = 0;
        foreach (var client in clients)
        {
            var previous = new int[created.Length];
            Array.Fill(previous, 1);
            foreach (var observation in client.Observations(lists))
            {
                if (observation.Sequence == 1) continue;
                for (var sequence = previous[observation.Run] + 1; sequence <= observation.Sequence; sequence++)
                {
                    var started = Volatile.Read(ref created[observation.Run][sequence - 2]);
                    if (started <= 0 || observation.At <= started || index >= samples.Length)
                        throw new InvalidDataException("Invalid or extra client latency observation.");
                    samples[index++] = Stopwatch.GetElapsedTime(started, observation.At).TotalMilliseconds;
                }
                previous[observation.Run] = observation.Sequence;
            }
        }
        if (index != expected) throw new InvalidDataException($"Dropped client updates: expected {expected}, received {index}.");
        Array.Sort(samples);
        return new(index, Percentile(samples, 0.50), Percentile(samples, 0.95), Percentile(samples, 0.99), samples[^1]);
    }

    private static double Percentile(double[] sorted, double percentile) => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
    private sealed record Observation(int Run, int Sequence, long At);

    private sealed class Client(HubConnection connection, int run, Dictionary<string, int> indexes, Guid version,
        int maximum, ConcurrentQueue<string> failures)
    {
        private readonly object gate = new();
        private readonly int[] lastList = new int[indexes.Count];
        private int lastRun;
        private readonly List<Observation> runObservations = [];
        private readonly List<Observation> listObservations = [];
        public HubConnection Connection => connection;

        public static async Task<Client> ConnectAsync(Uri origin, string jwt, int run, Dictionary<string, int> indexes,
            Guid version, int maximum, ConcurrentQueue<string> failures, CancellationToken token)
        {
            var connection = new HubConnectionBuilder().WithUrl(new Uri(origin, LiveHub.Path), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(jwt);
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.HttpMessageHandlerFactory = _ => new SocketsHttpHandler { UseProxy = false };
            }).AddMessagePackProtocol().Build();
            var client = new Client(connection, run, indexes, version, maximum, failures);
            connection.On<RunDelta>("RunDelta", client.Run);
            connection.On<ListDelta>("ListDelta", client.List);
            connection.On<LiveResync>("Resync", resource => failures.Enqueue(
                $"Unexpected {resource.Scope}/{resource.Id} resync during benchmark; exact delivery cannot be claimed."));
            connection.Closed += error => { failures.Enqueue("Benchmark client disconnected: " + error?.Message); return Task.CompletedTask; };
            try
            {
                await connection.StartAsync(token);
                foreach (var resource in new[] { ("type", Type), ("run", indexes.Single(pair => pair.Value == run).Key) })
                    if (await connection.InvokeAsync<LiveSubscriptionProblem?>("Subscribe", resource.Item1, resource.Item2, token) is { } problem)
                        throw new InvalidOperationException("Live benchmark subscription failed: " + problem.Status);
                return client;
            }
            catch { await connection.DisposeAsync(); throw; }
        }

        private void Run(RunDelta delta)
        {
            var at = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (!indexes.TryGetValue(delta.RunId, out var index) || index != run || delta.Events.Length == 0)
                { failures.Enqueue("Wrong run or empty run delta."); return; }
                foreach (var change in delta.Events)
                {
                    if (change.Seq != lastRun + 1 || change.Seq > maximum ||
                        change.Entity != 2 || change.Key != "node/task" ||
                        change.State != State(checked((int)change.Seq)))
                    { failures.Enqueue("Run event gap, duplicate, wrong state or extra event."); return; }
                    lastRun++;
                    runObservations.Add(new(run, lastRun, at));
                }
            }
        }

        private void List(ListDelta delta)
        {
            var at = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (delta.Scope != "type" || delta.Id != Type || delta.Items.Length == 0)
                { failures.Enqueue("Wrong or empty coalesced list delta."); return; }
                foreach (var item in delta.Items)
                {
                    if (!indexes.TryGetValue(item.RunId, out var index) || item.WorkflowId != Workflow ||
                        item.VersionId != version.ToString("D") || item.Status != (int)RunStatus.Running ||
                        item.LastSeq < lastList[index] || item.LastSeq > maximum)
                    { failures.Enqueue("List state regressed, exceeded committed sequence or changed identity."); return; }
                    if (item.LastSeq == lastList[index]) continue;
                    lastList[index] = checked((int)item.LastSeq);
                    listObservations.Add(new(index, lastList[index], at));
                }
            }
        }

        public bool CaughtUp(int sequence) { lock (gate) return lastRun >= sequence && lastList.All(value => value >= sequence); }
        public Observation[] Observations(bool lists) { lock (gate) return (lists ? listObservations : runObservations).ToArray(); }
    }
}
#endif
