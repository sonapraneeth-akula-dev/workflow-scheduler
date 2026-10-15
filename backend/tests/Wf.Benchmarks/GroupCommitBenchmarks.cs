using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.Engine.Core;

namespace Wf.Benchmarks;

public enum CommitMode { PerEvent, ImmediateBatch, TwoMillisecondBatch }
public sealed record GroupCommitMetrics(string Environment, CommitMode Mode, int Actors, int Events, int Commits,
    double MeanBatchSize, double AckMeanMs, double AckP95Ms, double AckP99Ms,
    double PersistenceMeanMs, double PersistenceP95Ms, double PersistenceP99Ms);

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10, invocationCount: 1)]
public class GroupCommitBenchmarks
{
    [Params(1, 32)]
    public int Actors { get; set; }

    [Params(CommitMode.PerEvent, CommitMode.ImmediateBatch, CommitMode.TwoMillisecondBatch)]
    public CommitMode Mode { get; set; }

    private PostgreSqlContainer? postgres;
    private NpgsqlDataSource dataSource = null!;
    private GroupCommitAppender appender = null!;
    private RunState[] runs = [];
    private string connectionString = "";
    private string schema = "";

    [GlobalSetup]
    public async Task Setup()
    {
        var external = System.Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
        if (external is null)
        {
            postgres = new PostgreSqlBuilder("postgres:17-alpine")
                .WithCommand("-c", "fsync=on", "-c", "full_page_writes=on", "-c", "synchronous_commit=on").Build();
            await postgres.StartAsync();
            external = postgres.GetConnectionString();
        }
        schema = $"group_commit_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(external))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        connectionString = new NpgsqlConnectionStringBuilder(external) { SearchPath = schema }.ConnectionString;
        dataSource = NpgsqlDataSource.Create(connectionString);
        foreach (var name in new[] { "0002_runs_events.sql", "0003_attempt_timers.sql" })
        {
            await using var migration = dataSource.CreateCommand(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", name)));
            await migration.ExecuteNonQueryAsync();
        }
        await using (var durability = dataSource.CreateCommand("SELECT current_setting('fsync'),current_setting('full_page_writes')"))
        await using (var reader = await durability.ExecuteReaderAsync())
            if (!await reader.ReadAsync() || reader.GetString(0) != "on" || reader.GetString(1) != "on")
                throw new InvalidOperationException("Durability benchmark requires fsync and full_page_writes enabled.");
        appender = new(dataSource, NullLogger<GroupCommitAppender>.Instance,
            window: Mode == CommitMode.TwoMillisecondBatch ? TimeSpan.FromMilliseconds(2) : TimeSpan.Zero,
            maxBatchSize: Mode == CommitMode.PerEvent ? 1 : 512);
        var store = new RunStore(dataSource, NullLogger<RunStore>.Instance);
        runs = new RunState[Actors];
        for (var actor = 0; actor < Actors; actor++)
        {
            var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
                new WorkflowMetadata("benchmark", ImmutableSortedDictionary<string, string>.Empty),
                message => throw new InvalidOperationException(message));
            await store.CreateAsync(run, "benchmark/group-commit", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
            await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow);
            await store.AppendAsync(run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow);
            runs[actor] = run;
        }
    }

    [Benchmark]
    public Task CommitActorEvents() => Task.WhenAll(runs.Select(CommitAsync));

    private async Task<GroupCommitReceipt> CommitAsync(RunState run)
    {
        var next = run.Snapshot.Tasks["node/task"] switch
        {
            ExecutionStatus.Ready or ExecutionStatus.Retrying => ExecutionStatus.Dispatched,
            ExecutionStatus.Dispatched => ExecutionStatus.Running,
            ExecutionStatus.Running => ExecutionStatus.Retrying,
            _ => throw new InvalidOperationException("Unexpected actor state in the group-commit benchmark.")
        };
        var change = run.CreateEvent(EventEntity.Task, "node/task", (int)next, DateTimeOffset.UtcNow);
        var receipt = await appender.AppendAsync(change);
        run.Apply(change);
        return receipt;
    }

    public async Task<GroupCommitMetrics> MeasureAsync(int eventCount = 1024)
    {
        if (eventCount < Actors || eventCount % Actors != 0)
            throw new ArgumentException("Probe event count must be a positive multiple of actor count.", nameof(eventCount));
        for (var warmup = 0; warmup < 4; warmup++) await CommitActorEvents();
        var samples = new List<(double AckMs, GroupCommitReceipt Receipt)>();
        for (var round = 0; round < eventCount / Actors; round++)
        {
            var measured = await Task.WhenAll(runs.Select(async run =>
            {
                var started = Stopwatch.GetTimestamp();
                var receipt = await CommitAsync(run);
                return (Stopwatch.GetElapsedTime(started).TotalMilliseconds, receipt);
            }));
            samples.AddRange(measured);
        }
        var store = new RunStore(dataSource, NullLogger<RunStore>.Instance);
        foreach (var run in runs)
        {
            var restored = await store.LoadAsync(run.Snapshot.RunId);
            if (restored.Snapshot.LastSequence != run.Snapshot.LastSequence ||
                restored.Snapshot.Tasks["node/task"] != run.Snapshot.Tasks["node/task"])
                throw new InvalidOperationException("Durable benchmark events did not restore to the acknowledged actor state.");
        }
        var commits = samples.Select(sample => sample.Receipt).DistinctBy(receipt => receipt.BatchId).ToArray();
        var ack = samples.Select(sample => sample.AckMs).Order().ToArray();
        var persistence = commits.Select(receipt => receipt.PersistenceTime.TotalMilliseconds).Order().ToArray();
        return new($"{RuntimeInformation.OSDescription}|{RuntimeInformation.FrameworkDescription}|{RuntimeInformation.ProcessArchitecture}",
            Mode, Actors, samples.Count, commits.Length, (double)samples.Count / commits.Length,
            ack.Average(), Percentile(ack, 0.95), Percentile(ack, 0.99),
            persistence.Average(), Percentile(persistence, 0.95), Percentile(persistence, 0.99));
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (appender is not null) await appender.DisposeAsync();
        if (dataSource is not null) await dataSource.DisposeAsync();
        if (schema.Length > 0)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
        if (postgres is not null) await postgres.DisposeAsync();
    }
}
