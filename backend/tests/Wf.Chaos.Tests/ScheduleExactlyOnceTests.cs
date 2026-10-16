using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.ControlPlane.Data;
using Wf.Sdk;

namespace Wf.Chaos.Tests;

public sealed class ScheduleExactlyOnceTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "Nightly")]
    public Task ScheduleExactlyOnce_one_thousand_minutely_schedules_for_one_real_hour_with_two_leader_kills() =>
        RunAsync(60, [20, 40]);

    internal async Task RunAsync(int minutes, int[] killMinutes)
    {
        const int schedules = 1000;
        var token = TestContext.Current.CancellationToken;
        var directory = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("WF_CHAOS_RESULTS") ?? Path.Combine("TestResults", "schedule-chaos"),
            $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        var events = new List<object>();
        var hosts = new List<EngineProcess>();
        var report = Path.Combine(directory, "result.json");
        var started = DateTimeOffset.UtcNow;
        await using var database = await ChaosDatabase.StartAsync(token);
        output.WriteLine($"Evidence: {report}");
        output.WriteLine("Real UTC clock; PostgreSQL 17 durable commits; production control-plane child processes.");
        try
        {
            var workflow = await SeedAsync(database.Source, token);
            hosts.Add(EngineProcess.Start(database.ConnectionString, directory, 0));
            hosts.Add(EngineProcess.Start(database.ConnectionString, directory, 1));
            var leader = await WaitForLeaderAsync(hosts, token);
            var first = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeSeconds());
            var end = first.AddMinutes(minutes);
            await ExecuteAsync(database.Source, """
                WITH phases AS (SELECT id,row_number() OVER(ORDER BY id)-1 AS phase FROM schedules)
                UPDATE schedules s SET next_fire_at=$1+phase*interval '60 milliseconds'
                    FROM phases p WHERE s.id=p.id;
                SELECT pg_notify('schedules','chaos/release/workflow')
                """, token, first);
            var scheduleStarts = new Dictionary<Guid, DateTimeOffset>();
            await using (var query = database.Source.CreateCommand("SELECT id,next_fire_at FROM schedules"))
            await using (var reader = await query.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                    scheduleStarts.Add(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1));
            Assert.Equal(schedules, scheduleStarts.Count);
            var observation = Stopwatch.StartNew();
            foreach (var minute in Enumerable.Range(0, minutes))
            {
                var slot = first.AddMinutes(minute);
                await DelayUntilAsync(slot.AddSeconds(15), token);
                AssertHostsAlive(hosts);
                if (killMinutes.Contains(minute))
                {
                    leader = await WaitForLeaderAsync(hosts, token);
                    var oldEpoch = await ScalarAsync<long>(database.Source, "SELECT epoch FROM leases WHERE name='engine'", token);
                    var killed = DateTimeOffset.UtcNow;
                    var pid = leader.Id;
                    await leader.KillAsync();
                    events.Add(new { kind = "leader-killed", pid, oldEpoch, at = killed });
                    leader = await WaitForLeaderAsync(hosts, token);
                    var newEpoch = await ScalarAsync<long>(database.Source, "SELECT epoch FROM leases WHERE name='engine'", token);
                    Assert.True(newEpoch > oldEpoch, "Hard kill must cause a new fenced leader epoch.");
                    events.Add(new { kind = "takeover-ready", pid = leader.Id, newEpoch, at = DateTimeOffset.UtcNow });
                    hosts.Add(EngineProcess.Start(database.ConnectionString, directory, hosts.Count));
                }

                var count = await ScalarAsync<long>(database.Source, "SELECT count(*) FROM schedule_fires", token);
                output.WriteLine($"Minute {minute + 1}/{minutes}: {count} persisted fire rows; leader PID {leader.Id}.");
                await File.WriteAllTextAsync(Path.Combine(directory, "progress.json"), JsonSerializer.Serialize(new
                {
                    gate = minutes == 60 ? "one-hour-acceptance" : "development-smoke-only",
                    first, end, minute = minute + 1, minutes, observedAt = DateTimeOffset.UtcNow,
                    persistedFireRows = count, leaderPid = leader.Id, events
                }), token);
            }
            await DelayUntilAsync(end, token);
            Assert.True(observation.Elapsed >= TimeSpan.FromMinutes(minutes) &&
                DateTimeOffset.UtcNow - first >= TimeSpan.FromMinutes(minutes),
                "The workload must run for the real wall-clock interval, not a virtualized hour.");
            // The next period may start at the end boundary; measure only the complete intended window.
            await ExecuteAsync(database.Source, "UPDATE schedules SET paused=true; SELECT pg_notify('schedules','chaos/release/workflow')", token);
            foreach (var host in hosts) await host.KillAsync();
            var measurements = new List<double>();
            var slots = new HashSet<(Guid, DateTimeOffset)>();
            var runIds = new HashSet<Guid>();
            long duplicateRuns = 0, silentMisses = 0, invalidRuns = 0, extraSlots = 0;
            var caughtUp = 0;
            await using (var query = database.Source.CreateCommand("""
                SELECT f.schedule_id,f.fire_at,f.outcome,f.reason,f.run_id,r.id,r.trigger,r.triggered_by,
                    extract(epoch FROM (pg_xact_commit_timestamp(r.xmin)-f.fire_at))::double precision,
                    (SELECT count(*) FROM run_snapshots p WHERE p.run_day=r.run_day AND p.run_id=r.id),
                    (SELECT count(*) FROM node_runs n WHERE n.run_day=r.run_day AND n.run_id=r.id)
                FROM schedule_fires f LEFT JOIN runs r
                    ON r.id=f.run_id AND r.run_day=uuid_v7_run_day(f.run_id)
                ORDER BY f.fire_at,f.schedule_id
                """))
            await using (var reader = await query.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var slot = reader.GetFieldValue<DateTimeOffset>(1);
                    if (slot >= end) continue;
                    if (slot < first || !scheduleStarts.TryGetValue(reader.GetGuid(0), out var initial) ||
                        (slot - initial).Ticks % TimeSpan.TicksPerMinute != 0)
                    {
                        extraSlots++;
                        continue;
                    }
                    if (!slots.Add((reader.GetGuid(0), slot))) duplicateRuns++;
                    if (reader.IsDBNull(4) || reader.IsDBNull(5))
                    {
                        silentMisses++;
                        continue;
                    }
                    if (!runIds.Add(reader.GetGuid(4))) duplicateRuns++;
                    if (reader.GetInt16(2) == 2) caughtUp++;
                    if (reader.GetInt16(2) is not (0 or 2) ||
                        reader.GetInt16(6) != (reader.GetInt16(2) == 2 ? 2 : 1) ||
                        reader.GetString(7) != "scheduler" || reader.IsDBNull(8) ||
                        reader.GetInt64(9) != 1 || reader.GetInt64(10) != 1)
                        invalidRuns++;
                    if (!reader.IsDBNull(8)) measurements.Add(reader.GetDouble(8));
                }
            }
            var expected = schedules * minutes;
            silentMisses += expected - slots.Count;
            await using var runCount = database.Source.CreateCommand("""
                SELECT count(*) FROM runs r WHERE NOT EXISTS(SELECT 1 FROM schedule_fires f WHERE f.run_id=r.id)
                    OR EXISTS(SELECT 1 FROM schedule_fires f WHERE f.run_id=r.id AND f.fire_at >= $1 AND f.fire_at < $2)
                """);
            runCount.Parameters.AddWithValue(first);
            runCount.Parameters.AddWithValue(end);
            var totalRuns = (long)(await runCount.ExecuteScalarAsync(token))!;
            duplicateRuns += Math.Max(0, totalRuns - runIds.Count);
            measurements.Sort();
            var p99 = measurements.Count == 0 ? double.PositiveInfinity : measurements[(int)Math.Ceiling(measurements.Count * .99) - 1];
            var summary = new
            {
                gate = minutes == 60 ? "one-hour-acceptance" : "development-smoke-only",
                started, finished = DateTimeOffset.UtcNow, first, end, schedules, minutes, expected,
                actualSlots = slots.Count, actualRuns = totalRuns, duplicateRuns, silentMisses, invalidRuns, extraSlots,
                caughtUp, phaseSpacingMilliseconds = 60, p99Seconds = p99, thresholdSeconds = 2,
                maximumSeconds = measurements.LastOrDefault(),
                durability = database.Settings, events, workflow,
                pass = duplicateRuns == 0 && silentMisses == 0 && invalidRuns == 0 && extraSlots == 0 &&
                    measurements.Count == expected && p99 <= 2
            };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), token);
            output.WriteLine(JsonSerializer.Serialize(summary));
            Assert.Equal(0, duplicateRuns);
            Assert.Equal(0, silentMisses);
            Assert.Equal(0, invalidRuns);
            Assert.Equal(0, extraSlots);
            Assert.Equal(expected, measurements.Count);
            Assert.Equal(killMinutes.Length, events.Count(item =>
                JsonSerializer.Serialize(item).Contains("\"leader-killed\"", StringComparison.Ordinal)));
            Assert.True(p99 <= 2, $"Intended-fire → durable creation p99 {p99:F6}s exceeds 2s. Evidence: {report}");
        }
        catch (Exception error)
        {
            if (!File.Exists(report))
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    gate = minutes == 60 ? "one-hour-acceptance" : "development-smoke-only",
                    started, finished = DateTimeOffset.UtcNow, pass = false, error = error.ToString(), events
                }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            throw;
        }
        finally
        {
            foreach (var host in hosts) await host.DisposeAsync();
        }
    }

    internal static async Task<string> SeedAsync(NpgsqlDataSource source, CancellationToken token, int schedules = 1000)
    {
        const string workflow = "chaos/release/workflow";
        var definition = Workflow.Define(workflow, "1.0.0", builder =>
            builder.Type("release").Node("work", node => node.Task("task", _ => { })));
        var manifest = System.Text.Encoding.UTF8.GetString(ManifestWriter.WriteUtf8(definition));
        await ExecuteAsync(source, """
            INSERT INTO teams(id,name) VALUES('chaos','Chaos');
            INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
                VALUES('chaos/release','chaos','release','release','Chaos load','{"fields":[]}');
            INSERT INTO workflows(id,type_id) VALUES('chaos/release/workflow','chaos/release');
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,'chaos/release/workflow','1.0.0',$2::jsonb,
                'packages/'||repeat('00',32)||'.zip',decode(repeat('00',32),'hex'),1,
                'sources/'||repeat('00',32),decode(repeat('00',32),'hex'),1,repeat('a',40),'chaos','chaos','1.0.0');
            UPDATE workflows SET active_version_id=$1,latest_version_id=$1;
            INSERT INTO schedules(id,workflow_id,origin,code_key,name,timezone,interval_s,overlap_policy,
                catchup_policy,created_by,updated_by)
                SELECT gen_random_uuid(),'chaos/release/workflow',0,'timer-'||i,'Timer '||i,'Etc/UTC',60,2,1,'chaos','chaos'
                FROM generate_series(1,$3) i;
            INSERT INTO run_series(id,workflow_id,schedule_id) SELECT gen_random_uuid(),workflow_id,id FROM schedules;
            """, token, Guid.CreateVersion7(), manifest, schedules);
        return workflow;
    }

    private static async Task<EngineProcess> WaitForLeaderAsync(List<EngineProcess> hosts, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while (true)
        {
            AssertHostsAlive(hosts);
            foreach (var host in hosts.Where(host => !host.Killed))
                if (await host.IsLeaderAsync(timeout.Token)) return host;
            await Task.Delay(100, timeout.Token);
        }
    }

    private static void AssertHostsAlive(List<EngineProcess> hosts)
    {
        foreach (var host in hosts.Where(host => !host.Killed))
            Assert.False(host.HasExited, $"Engine PID {host.Id} exited unexpectedly; inspect {host.LogPath}.");
    }

    private static async Task DelayUntilAsync(DateTimeOffset until, CancellationToken token)
    {
        while (until > DateTimeOffset.UtcNow)
            await Task.Delay(TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond,
                (until - DateTimeOffset.UtcNow).Ticks)), token);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql, CancellationToken token, params object[] values)
    {
        for (var index = values.Length; index > 0; index--)
            sql = sql.Replace($"${index}", $"@p{index}", StringComparison.Ordinal);
        await using var query = source.CreateCommand(sql);
        for (var index = 0; index < values.Length; index++)
            query.Parameters.AddWithValue($"p{index + 1}", values[index]);
        await query.ExecuteNonQueryAsync(token);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql, CancellationToken token)
    {
        await using var query = source.CreateCommand(sql);
        return (T)(await query.ExecuteScalarAsync(token) ?? throw new InvalidDataException("Missing scalar result."));
    }

    internal sealed class EngineProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly HttpClient client;
        private readonly Task logging;
        public int Id => process.Id;
        public bool Killed { get; private set; }
        public bool HasExited => process.HasExited;
        public string LogPath { get; }

        private EngineProcess(Process process, int port, string logPath)
        {
            this.process = process;
            LogPath = logPath;
            client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
            logging = CaptureAsync();
        }

        public static EngineProcess Start(string connectionString, string directory, int index)
        {
            using var api = new TcpListener(IPAddress.Loopback, 0);
            using var engine = new TcpListener(IPAddress.Loopback, 0);
            api.Start();
            engine.Start();
            var port = ((IPEndPoint)api.LocalEndpoint).Port;
            var enginePort = ((IPEndPoint)engine.LocalEndpoint).Port;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(Migrator).Assembly.Location);
            start.ArgumentList.Add("--dev");
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ConnectionStrings__Workflow"] = connectionString;
            start.Environment["Engine__Port"] = enginePort.ToString(CultureInfo.InvariantCulture);
            start.Environment["Api__Port"] = port.ToString(CultureInfo.InvariantCulture);
            start.Environment["Storage__Root"] = Path.Combine(directory, $"storage-{index}");
            start.Environment["Storage__UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            start.Environment["Logging__LogLevel__Default"] = "Warning";
            api.Stop();
            engine.Stop();
            return new(Process.Start(start) ?? throw new InvalidOperationException("Could not start the actual engine."),
                port, Path.Combine(directory, $"engine-{index}.log"));
        }

        public async Task<bool> IsLeaderAsync(CancellationToken token)
        {
            try
            {
                using var response = await client.GetAsync("/healthz/leader", token);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { return false; }
        }

        public async Task KillAsync()
        {
            if (Killed) return;
            Killed = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await logging;
        }

        private async Task CaptureAsync()
        {
            await using var stdout = File.Create(LogPath);
            await using var stderr = File.Create(LogPath + ".stderr");
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(stdout),
                process.StandardError.BaseStream.CopyToAsync(stderr));
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            client.Dispose();
            process.Dispose();
        }
    }

    internal sealed class ChaosDatabase : IAsyncDisposable
    {
        private PostgreSqlContainer? container;
        private string? schema;
        public string ConnectionString { get; private set; } = "";
        public NpgsqlDataSource Source { get; private set; } = null!;
        public Dictionary<string, string> Settings { get; } = [];

        public static async Task<ChaosDatabase> StartAsync(CancellationToken token, bool requireCommitTimestamps = true)
        {
            var database = new ChaosDatabase();
            try
            {
                var external = Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
                if (external is null)
                {
                    database.container = new PostgreSqlBuilder("postgres:17-alpine")
                        .WithCommand("-c", "fsync=on", "-c", "full_page_writes=on", "-c", "synchronous_commit=on",
                            "-c", "track_commit_timestamp=on", "-c", "max_connections=200").Build();
                    await database.container.StartAsync(token);
                    database.ConnectionString = database.container.GetConnectionString();
                }
                else
                {
                    database.schema = $"chaos_{Guid.NewGuid():N}";
                    await using var admin = NpgsqlDataSource.Create(external);
                    await ExecuteAsync(admin, $"CREATE SCHEMA \"{database.schema}\"", token);
                    database.ConnectionString = new NpgsqlConnectionStringBuilder(external)
                        { SearchPath = database.schema }.ConnectionString;
                }
                database.Source = NpgsqlDataSource.Create(database.ConnectionString);
                foreach (var setting in new[] { "server_version_num", "fsync", "full_page_writes", "synchronous_commit", "track_commit_timestamp" })
                    database.Settings[setting] = await ScalarAsync<string>(database.Source, $"SHOW {setting}", token);
                Assert.InRange(int.Parse(database.Settings["server_version_num"], CultureInfo.InvariantCulture), 170000, 179999);
                foreach (var setting in database.Settings.Where(pair => pair.Key != "server_version_num" &&
                    (requireCommitTimestamps || pair.Key != "track_commit_timestamp")))
                    Assert.Equal("on", setting.Value);
                foreach (var script in Migrator.LoadScripts())
                    await ExecuteAsync(database.Source, script.Contents, token);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Source is not null) await Source.DisposeAsync();
            if (container is not null) await container.DisposeAsync();
            else if (schema is not null)
            {
                await using var admin = NpgsqlDataSource.Create(ConnectionString);
                await ExecuteAsync(admin, $"DROP SCHEMA \"{schema}\" CASCADE", CancellationToken.None);
            }
        }
    }
}
