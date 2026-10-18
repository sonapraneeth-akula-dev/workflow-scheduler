using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.Contracts;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Data;
using Wf.Testing.RunQueries;

namespace Wf.Benchmarks;

public sealed record RunQueryCaseMetrics(string Name, int Samples, int Warmups, int PageRows, bool HasNextPage,
    double P50Ms, double P95Ms, double P99Ms, double MaxMs, bool Passed);
public sealed record RunQueryBenchmarkReport(bool Passed, int SeededRows, int VisibleRows, int TeamCount,
    int TypeCount, int WorkflowCount, int SeriesCount, DateTimeOffset FirstCreatedAt, DateTimeOffset LastCreatedAt,
    string Os, string Framework, string Architecture, int Processors, bool ServerGc, string Postgres,
    string DatabaseMode, string Authentication, string Measurement, string Gate,
    ImmutableDictionary<string, string> Durability, ImmutableArray<RunQueryCaseMetrics> Cases);

public static class RunQueryBenchmarks
{
    public const int Samples = 100;
    public const int Warmups = 10;

    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var report = await MeasureAsync(timeout.Token);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            var directory = Path.Combine("BenchmarkDotNet.Artifacts", "run-query", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(path, json, timeout.Token);
            Console.WriteLine(json);
            Console.WriteLine("Report: " + Path.GetFullPath(path));
            return report.Passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Run-query benchmark failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    public static async Task<RunQueryBenchmarkReport> MeasureAsync(CancellationToken token)
    {
        PostgreSqlContainer? postgres = null;
        NpgsqlDataSource? source = null;
        var external = Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
        var mode = external is null ? "isolated postgres:17-alpine container" : "WF_TEST_POSTGRES unique schema";
        var schema = "run_query_benchmark_" + Guid.NewGuid().ToString("N");
        var schemaCreated = false;
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
                schemaCreated = true;
            }
            var connectionString = new NpgsqlConnectionStringBuilder(external) { SearchPath = schema }.ConnectionString;
            Migrator.Upgrade(connectionString, NullLogger.Instance);
            source = NpgsqlDataSource.Create(connectionString);
            var settings = ImmutableDictionary.CreateBuilder<string, string>();
            string postgresVersion;
            await using (var query = source.CreateCommand("""
                SELECT current_setting('fsync'),current_setting('full_page_writes'),current_setting('synchronous_commit'),version()
                """))
            await using (var reader = await query.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new InvalidDataException("PostgreSQL settings are missing.");
                settings.Add("fsync", reader.GetString(0));
                settings.Add("full_page_writes", reader.GetString(1));
                settings.Add("synchronous_commit", reader.GetString(2));
                postgresVersion = reader.GetString(3);
            }
            if (settings.Values.Any(value => value != "on"))
                throw new InvalidOperationException("Run-query measurement requires PostgreSQL durability settings ON.");
            var seed = await RunSeeder.SeedAsync(source, token);
            await using (var count = source.CreateCommand("SELECT count(*) FROM runs"))
                if (await count.ExecuteScalarAsync(token) is not long actual || actual != RunSeeder.RowCount)
                    throw new InvalidDataException("Run-query benchmark requires exactly 100,000 persisted rows.");
            await using var server = await RunQueryServer.Start(source, token);
            var jwt = server.Jwt(seed.ViewerGroup);
            var team = seed.Teams[0];
            var path = "/api/v1/runs?team=" + team + "&limit=100";
            var cursor = "";
            for (var index = 0; index < 50; index++)
            {
                var page = await Read(server, path + cursor, jwt);
                if (page.Items.Count != 100 || page.NextCursor is null)
                    throw new InvalidDataException("Deep-cursor preparation exhausted the seeded visible history.");
                cursor = "&cursor=" + Uri.EscapeDataString(page.NextCursor);
            }
            var from = seed.Rows[20_000].CreatedAt;
            var to = seed.Rows[80_000].CreatedAt;
            var window = "&from=" + Uri.EscapeDataString(from.ToString("O", System.Globalization.CultureInfo.InvariantCulture)) +
                "&to=" + Uri.EscapeDataString(to.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            var first = seed.Rows[0];
            Case[] cases =
            [
                new("unscoped-visible-list", "/api/v1/runs?limit=100", _ => true),
                new("team-list", path, _ => true),
                new("type", path + "&type=release", row => row.Type == "release"),
                new("status", path + "&status=Succeeded", row => row.Status == RunStatus.Succeeded),
                new("metadata-and", path + "&meta.environment=prod&meta.region=west",
                    row => row.Environment == "prod" && row.Region == "west"),
                new("time-range", path + window, row => row.CreatedAt >= from && row.CreatedAt < to),
                new("combined", path + "&type=perf&status=Succeeded&meta.environment=prod&meta.region=west" + window,
                    row => row.Type == "perf" && row.Status == RunStatus.Succeeded &&
                        row.Environment == "prod" && row.Region == "west" && row.CreatedAt >= from && row.CreatedAt < to),
                new("workflow", path + "&workflowId=" + Uri.EscapeDataString(first.WorkflowId), row => row.WorkflowId == first.WorkflowId),
                new("series", path + "&seriesId=" + first.SeriesId.ToString("D"), row => row.SeriesId == first.SeriesId),
                new("deep-keyset", path + cursor, _ => true, Skip: 5000)
            ];
            var results = ImmutableArray.CreateBuilder<RunQueryCaseMetrics>(cases.Length);
            foreach (var scenario in cases)
            {
                var expected = seed.Rows.Where(row => row.Team == team && scenario.Matches(row))
                    .OrderByDescending(row => row.CreatedAt).Skip(scenario.Skip).Take(101).ToArray();
                var more = expected.Length > 100;
                var pageRows = expected.Take(100).ToArray();
                if (pageRows.Length == 0) throw new InvalidDataException("A benchmark scenario has no positive seeded matches.");
                for (var warmup = 0; warmup < Warmups; warmup++)
                    Verify(await Read(server, scenario.Path, jwt), pageRows, more);
                var samples = new double[Samples];
                for (var index = 0; index < samples.Length; index++)
                {
                    var started = Stopwatch.GetTimestamp();
                    Verify(await Read(server, scenario.Path, jwt), pageRows, more);
                    samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                Array.Sort(samples);
                var p95 = samples[94];
                results.Add(new(scenario.Name, samples.Length, Warmups, pageRows.Length, more, samples[49], p95,
                    samples[98], samples[^1], samples.All(value => double.IsFinite(value) && value > 0) &&
                        PerformanceGate.RunQueryWithinLimit(p95)));
            }
            var measured = results.MoveToImmutable();
            return new(measured.All(result => result.Passed), seed.Rows.Length,
                seed.Rows.Count(row => row.Team == team), seed.Teams.Length,
                seed.Teams.Length * seed.Types.Length, seed.Rows.Select(row => row.WorkflowId).Distinct().Count(),
                seed.Rows.Select(row => row.SeriesId).Distinct().Count(), seed.Rows[0].CreatedAt, seed.Rows[^1].CreatedAt,
                RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, GCSettings.IsServerGC, postgresVersion,
                mode, "Signed JWT, production authentication and fresh PostgreSQL Viewer grants; no global Admin",
                "Sequential warm loopback Kestrel HTTP/1.1; request through complete body, typed JSON and exact oracle validation; no UI rendering or remote-network time; no forced query plans or GC",
                "Every scenario: 100 samples after 10 warmups, nearest-rank p95 < 300 ms; exact persisted 100k rows and exact authorized page identities",
                settings.ToImmutable(), measured);
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
            try
            {
                if (schemaCreated)
                {
                    await using var admin = new NpgsqlConnection(external);
                    await admin.OpenAsync(CancellationToken.None);
                    await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
                    await drop.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            finally { if (postgres is not null) await postgres.DisposeAsync(); }
        }
    }

    private sealed record Case(string Name, string Path, Func<SeededQueryRun, bool> Matches, int Skip = 0);

    private static async Task<RunQueryPage> Read(RunQueryServer server, string path, string jwt)
    {
        using var response = await server.Get(path, jwt);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new InvalidDataException($"Run-query benchmark received HTTP {(int)response.StatusCode} or a cacheable response.");
        return await response.Content.ReadFromJsonAsync(RunQueryJsonContext.Default.RunQueryPage)
            ?? throw new InvalidDataException("Run-query benchmark received a null page.");
    }

    private static void Verify(RunQueryPage page, IReadOnlyList<SeededQueryRun> expected, bool more)
    {
        if (page.Items.Count != expected.Count || (page.NextCursor is not null) != more)
            throw new InvalidDataException("Run-query benchmark returned an incorrect page boundary.");
        for (var index = 0; index < expected.Count; index++)
        {
            var actual = page.Items[index];
            var row = expected[index];
            if (actual.Id != row.Id || actual.Team != row.Team || actual.Type != row.Type ||
                actual.WorkflowId != row.WorkflowId || actual.SeriesId != row.SeriesId ||
                actual.Status != row.Status.ToString() || actual.CreatedAt != row.CreatedAt ||
                actual.StartedAt != (row.Status == RunStatus.Queued ? null : row.CreatedAt.AddSeconds(1)) ||
                actual.EndedAt != ((short)row.Status < 3 ? null : row.CreatedAt.AddSeconds(2)))
                throw new InvalidDataException("Run-query benchmark returned data that disagrees with its independent seed oracle.");
        }
    }
}
