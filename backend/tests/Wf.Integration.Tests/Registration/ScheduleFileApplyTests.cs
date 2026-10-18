using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Scheduler;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed class ScheduleFileApplyTests(RegistrationDatabase database, ITestOutputHelper output)
    : IClassFixture<RegistrationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fifty_schedule_file_replacements_during_hosted_firing_preserve_every_intended_slot_and_stop_removed_instances()
    {
        Assert.Equal("on", await Scalar("SHOW fsync"));
        Assert.Equal("on", await Scalar("SHOW full_page_writes"));
        Assert.Equal("on", await Scalar("SHOW synchronous_commit"));
        await using var server = await Server.Start(database.DataSource, runs: true);
        server.Clock.Now = DateTimeOffset.FromUnixTimeSeconds(server.Clock.Now.ToUnixTimeSeconds());
        var request = await Request(server);
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables =
            [
                new("env", VariableType.String, true),
                new("generation", VariableType.Int, true),
                new("region", VariableType.String, @default: JsonSerializer.SerializeToElement("fallback"))
            ]
        };
        request = request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest),
            Schedules = File(Definitions(0)),
            ScheduleSourcePath = "workflow.schedules.json"
        };
        using var registered = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var registration = await Json(registered);
        var version = registration.GetProperty("workflowVersion").GetGuid();
        var workflow = request.WorkflowId!;
        await WaitFor(() => Task.FromResult(server.Versions.IsReady && HasVersion()), "active version cache");
        var model = new Dictionary<string, ModeledSchedule>(StringComparer.Ordinal);
        var expected = new Dictionary<(Guid Schedule, DateTimeOffset Slot), Definition>();
        var configs = new Dictionary<Guid, string>
        {
            [registration.GetProperty("scheduleConfigVersion").GetGuid()] = request.Schedules!
        };
        await Accept(Definitions(0));

        var application = "wf-fifty-files-" + Guid.NewGuid().ToString("N");
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = application
        }.ConnectionString);
        await using var engine = new Engine(source, server);
        await engine.Start();
        await WaitFor(() => Task.FromResult(engine.Scheduler.IsReady), "hosted scheduler readiness");
        await Advance(4);
        var overlappingReplacements = 0;
        var atomicObservations = 0;
        for (var generation = 1; generation <= 50; generation++)
        {
            // Keep mutable timers outside the commit boundary; the invariant anchor must fire across it.
            while (model.Values.Any(schedule => schedule.Definition.Key != "anchor" &&
                       schedule.Next is { } next && next <= server.Clock.Now.AddSeconds(1)))
                await Advance(1);
            var definitions = Definitions(generation);
            var content = File(definitions);
            var fileRequest = new RegistrationRequest(generation.ToString("x40"), $"Load generation {generation}",
                workflow, Schedules: content, ScheduleSourcePath: request.ScheduleSourcePath);
            await using (var blocker = await database.DataSource.OpenConnectionAsync(Token))
            await using (var transaction = await blocker.BeginTransactionAsync(Token))
            {
                await using (var hold = new NpgsqlCommand("SELECT id FROM schedules WHERE id=$1 FOR UPDATE", blocker, transaction))
                {
                    hold.Parameters.AddWithValue(model["anchor"].Id);
                    await hold.ExecuteScalarAsync(Token);
                }
                MoveClock();
                await WaitFor(async () => (bool)(await Scalar("""
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                    WHERE datname=current_database() AND application_name=$1
                        AND wait_event_type='Lock' AND query LIKE '%FOR UPDATE OF s%'
                        AND $2=ANY(pg_blocking_pids(pid)))
                    """, application, blocker.ProcessID))!, $"generation {generation}: in-flight hosted fire");
                var applying = server.Post(fileRequest);
                try
                {
                    await WaitFor(async () => (bool)(await Scalar("""
                        SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                        WHERE datname=current_database() AND wait_event_type='Lock'
                            AND query LIKE 'INSERT INTO schedules%')
                        """))!, $"generation {generation}: registration contends with hosted fire");
                    overlappingReplacements++;
                    await ObserveAtomicFile();
                    await transaction.CommitAsync(Token);
                    do
                    {
                        await ObserveAtomicFile();
                        await Task.Delay(1, Token);
                    } while (!applying.IsCompleted);
                    using var response = await applying;
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var json = await Json(response);
                    Assert.Equal("scheduleConfigVersion", Assert.Single(json.EnumerateObject()).Name);
                    var config = json.GetProperty("scheduleConfigVersion").GetGuid();
                    Assert.True(configs.TryAdd(config, content), $"Generation {generation} did not create a new snapshot.");
                    Assert.Equal(config, await Scalar("SELECT active_schedule_config_id FROM workflows WHERE id=$1", workflow));
                    await Accept(definitions);
                    await ObserveAtomicFile();
                }
                finally
                {
                    await transaction.DisposeAsync();
                    using var response = await applying;
                }
            }
            await Drain();
            var removed = model.Values.Where(schedule => schedule.Next is null)
                .ToDictionary(schedule => schedule.Id, schedule => expected.Keys.Count(key => key.Schedule == schedule.Id));
            var before = expected.Count;
            await Advance(4);
            foreach (var definition in definitions)
                Assert.Contains(expected.Skip(before), fire => fire.Value == definition);
            foreach (var (id, count) in removed)
                Assert.Equal((long)count, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", id));
            await AssertCursors();
        }
        await Advance(8);
        await AssertCursors();
        await engine.Stop();

        Assert.Equal(50, overlappingReplacements);
        Assert.True(atomicObservations >= 150);
        Assert.Equal(51, configs.Count);
        Assert.Equal(3, model.Count);
        Assert.Equal(346, expected.Count);
        Assert.Equal(262, expected.Keys.Count(key => key.Schedule == model["anchor"].Id));
        Assert.Equal(44, expected.Keys.Count(key => key.Schedule == model["blue"].Id));
        Assert.Equal(40, expected.Keys.Count(key => key.Schedule == model["green"].Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));
        Assert.Equal(3L, await Scalar("SELECT count(*) FROM schedules WHERE workflow_id=$1 AND origin=0", workflow));
        Assert.Equal(3L, await Scalar("SELECT count(*) FROM run_series WHERE workflow_id=$1", workflow));
        Assert.Equal(51L, await Scalar("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", workflow));
        Assert.Equal(version, await Scalar("SELECT active_version_id FROM workflows WHERE id=$1", workflow));
        Assert.Equal(version, await Scalar("SELECT latest_version_id FROM workflows WHERE id=$1", workflow));
        Assert.Equal((long)expected.Count, await Scalar("SELECT count(*) FROM runs WHERE workflow_id=$1", workflow));
        var actual = new HashSet<(Guid Schedule, DateTimeOffset Slot)>();
        var runs = new HashSet<Guid>();
        await using (var query = database.DataSource.CreateCommand("""
            SELECT f.schedule_id,f.fire_at,f.outcome,f.reason,f.run_id,r.series_id,r.version_id,r.trigger,
                r.triggered_by,r.variables::text,
                (SELECT count(*) FROM run_snapshots p WHERE p.run_day=r.run_day AND p.run_id=r.id),
                (SELECT count(*) FROM node_runs n WHERE n.run_day=r.run_day AND n.run_id=r.id),
                (SELECT count(*) FROM commands c WHERE c.run_id=r.id)
            FROM schedule_fires f JOIN schedules s ON s.id=f.schedule_id
            LEFT JOIN runs r ON r.id=f.run_id AND r.run_day=uuid_v7_run_day(f.run_id)
            WHERE s.workflow_id=$1 ORDER BY f.fire_at,s.code_key
            """))
        {
            query.Parameters.AddWithValue(workflow);
            await using var reader = await query.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                var slot = (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1));
                Assert.True(actual.Add(slot), $"Duplicate intended slot {slot}.");
                Assert.True(expected.TryGetValue(slot, out var definition), $"Unexpected intended slot {slot}.");
                Assert.Equal((short)0, reader.GetInt16(2));
                Assert.True(reader.IsDBNull(3), $"Slot {slot} was skipped or substituted by catch-up.");
                Assert.False(reader.IsDBNull(4));
                var run = reader.GetGuid(4);
                Assert.True(runs.Add(run), $"Run {run} substituted for more than one slot.");
                Assert.Equal(model[definition.Key].Series, reader.GetGuid(5));
                Assert.Equal(version, reader.GetGuid(6));
                Assert.Equal((short)1, reader.GetInt16(7));
                Assert.Equal("scheduler", reader.GetString(8));
                using var variables = JsonDocument.Parse(reader.GetString(9));
                AssertVariables(variables.RootElement, definition);
                Assert.Equal(1L, reader.GetInt64(10));
                Assert.Equal(1L, reader.GetInt64(11));
                Assert.Equal(0L, reader.GetInt64(12));
                var stored = await engine.Store.LoadAsync(run, Token);
                AssertVariables(JsonSerializer.SerializeToElement(stored.Snapshot.Variables), definition);
                Assert.Equal(run, stored.Snapshot.RunId);
                Assert.Equal("release", stored.Snapshot.Metadata.Type);
                Assert.Single(stored.Snapshot.Nodes);
                Assert.Single(stored.Snapshot.Tasks);
            }
        }
        Assert.Equal(expected.Keys.OrderBy(key => key.Schedule).ThenBy(key => key.Slot),
            actual.OrderBy(key => key.Schedule).ThenBy(key => key.Slot));
        foreach (var (config, content) in configs)
        {
            Assert.Equal(content, await Scalar("SELECT content FROM schedule_config_versions WHERE id=$1", config));
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(content)),
                Assert.IsType<byte[]>(await Scalar("SELECT content_sha256 FROM schedule_config_versions WHERE id=$1", config)));
        }
        Assert.Empty(server.Failures);
        output.WriteLine($"50 contended committed replacements; {atomicObservations} coherent snapshots; " +
            $"{expected.Count} exact normal intended slots/runs; " +
            string.Join(", ", model.Select(pair => $"{pair.Key}={expected.Keys.Count(key => key.Schedule == pair.Value.Id)}")));

        bool HasVersion()
        {
            try { return server.Versions.GetActive(workflow).VersionId == version; }
            catch (KeyNotFoundException) { return false; }
        }

        void MoveClock()
        {
            server.Clock.Now = server.Clock.Now.AddSeconds(1);
            foreach (var schedule in model.Values)
            {
                if (schedule.Next is not { } next || next > server.Clock.Now) continue;
                Assert.Equal(server.Clock.Now, next);
                Assert.True(expected.TryAdd((schedule.Id, next), schedule.Definition));
                schedule.Next = next.AddSeconds(schedule.Definition.Interval);
            }
        }

        async Task Advance(int seconds)
        {
            for (var second = 0; second < seconds; second++)
            {
                MoveClock();
                await Drain();
            }
        }

        Task Drain() => WaitFor(async () =>
        {
            Assert.False(engine.Scheduler.ExecuteTask!.IsCompleted, engine.Scheduler.ExecuteTask.Exception?.ToString());
            var count = (long)(await Scalar("""
                SELECT count(*) FROM schedule_fires f JOIN schedules s ON s.id=f.schedule_id WHERE s.workflow_id=$1
                """, workflow))!;
            Assert.True(count <= expected.Count, $"Unexpected fire: actual {count}, expected {expected.Count}.");
            return count == expected.Count;
        }, $"all {expected.Count} enumerated intended slots at {server.Clock.Now:O}");

        async Task Accept(Definition[] definitions)
        {
            foreach (var schedule in model.Values)
                if (!definitions.Any(definition => definition.Key == schedule.Definition.Key)) schedule.Next = null;
            await using var query = database.DataSource.CreateCommand("""
                SELECT s.code_key,s.id,r.id FROM schedules s JOIN run_series r ON r.schedule_id=s.id
                WHERE s.workflow_id=$1 AND s.origin=0
                """);
            query.Parameters.AddWithValue(workflow);
            await using var reader = await query.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                var key = reader.GetString(0);
                var definition = definitions.SingleOrDefault(definition => definition.Key == key);
                if (model.TryGetValue(key, out var schedule))
                {
                    Assert.Equal(schedule.Id, reader.GetGuid(1));
                    Assert.Equal(schedule.Series, reader.GetGuid(2));
                    if (definition is null) continue;
                    if (schedule.Next is null || schedule.Definition.Interval != definition.Interval)
                        schedule.Next = server.Clock.Now.AddSeconds(definition.Interval);
                    schedule.Definition = definition;
                }
                else
                {
                    Assert.NotNull(definition);
                    model.Add(key, new(reader.GetGuid(1), reader.GetGuid(2), definition,
                        server.Clock.Now.AddSeconds(definition.Interval)));
                }
            }
            Assert.Equal(definitions.Length, model.Values.Count(schedule => schedule.Next is not null));
        }

        async Task AssertCursors()
        {
            foreach (var schedule in model.Values)
            {
                Assert.Equal((object?)schedule.Next ?? DBNull.Value,
                    await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
                Assert.Equal(schedule.Next is null, await Scalar(
                    "SELECT paused AND deleted_at IS NOT NULL FROM schedules WHERE id=$1", schedule.Id));
                Assert.Equal(schedule.Series, await Scalar("SELECT id FROM run_series WHERE schedule_id=$1", schedule.Id));
            }
        }

        async Task ObserveAtomicFile()
        {
            Assert.Equal(true, await Scalar("""
                SELECT w.active_version_id=$2 AND w.latest_version_id=$2
                    AND NOT EXISTS (
                        SELECT 1 FROM jsonb_array_elements(c.content::jsonb->'instances') i
                        LEFT JOIN schedules s ON s.workflow_id=w.id AND s.origin=0 AND s.code_key=i->>'id'
                        WHERE s.id IS NULL OR s.deleted_at IS NOT NULL OR s.paused OR s.next_fire_at IS NULL
                            OR s.config_version_id IS DISTINCT FROM c.id
                            OR s.timezone IS DISTINCT FROM i->>'timezone'
                            OR s.interval_s IS DISTINCT FROM extract(epoch FROM (i->>'interval')::interval)
                            OR s.version_pin IS DISTINCT FROM i->>'versionPin'
                            OR s.overlap_policy<>2 OR s.catchup_policy<>0
                            OR s.variables IS DISTINCT FROM ('{"region":"fallback"}'::jsonb || (i->'variables'))
                    )
                    AND NOT EXISTS (
                        SELECT 1 FROM schedules s WHERE s.workflow_id=w.id AND s.origin=0
                            AND s.deleted_at IS NULL AND NOT EXISTS (
                                SELECT 1 FROM jsonb_array_elements(c.content::jsonb->'instances') i
                                WHERE i->>'id'=s.code_key)
                    )
                FROM workflows w JOIN schedule_config_versions c ON c.id=w.active_schedule_config_id
                WHERE w.id=$1
                """, workflow, version));
            atomicObservations++;
        }
    }

    private static Definition[] Definitions(int generation)
    {
        var definitions = new List<Definition> { new("anchor", 1, 0) };
        if (generation % 5 != 2) definitions.Add(new("blue", 3 + generation % 2, generation));
        if (generation % 5 != 0) definitions.Add(new("green", 4 - generation % 2, generation));
        return definitions.ToArray();
    }

    private static string File(IEnumerable<Definition> definitions) => JsonSerializer.Serialize(new
    {
        instances = definitions.Select(definition => new
        {
            id = definition.Key,
            interval = TimeSpan.FromSeconds(definition.Interval).ToString("c"),
            timezone = "Etc/UTC",
            versionPin = "1.*",
            overlap = "Concurrent",
            catchUp = "None",
            variables = new { env = definition.Environment, generation = definition.Generation }
        })
    });

    private static void AssertVariables(JsonElement variables, Definition definition)
    {
        Assert.Equal(3, variables.EnumerateObject().Count());
        Assert.Equal(definition.Environment, variables.GetProperty("env").GetString());
        Assert.Equal(definition.Generation, variables.GetProperty("generation").GetInt32());
        Assert.Equal("fallback", variables.GetProperty("region").GetString());
    }

    private async Task<object?> Scalar(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        var result = await query.ExecuteScalarAsync(Token);
        return result is DateTime timestamp ? new DateTimeOffset(timestamp) : result;
    }

    private static async Task WaitFor(Func<Task<bool>> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {description}.");
            await Task.Delay(10, Token);
        }
    }

    private sealed record Definition(string Key, int Interval, int Generation)
    {
        public string Environment => $"{Key}-{Generation}";
    }

    private sealed class ModeledSchedule(Guid id, Guid series, Definition definition, DateTimeOffset next)
    {
        public Guid Id { get; } = id;
        public Guid Series { get; } = series;
        public Definition Definition { get; set; } = definition;
        public DateTimeOffset? Next { get; set; } = next;
    }

    private sealed class Engine : IAsyncDisposable
    {
        private readonly LeaseManager lease;
        private readonly RunRecovery recovery;
        private bool stopped;
        public RunStore Store { get; }
        public ScheduleFireService Scheduler { get; }

        public Engine(NpgsqlDataSource source, Server server)
        {
            lease = new(source, new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5301")),
                NullLogger<LeaseManager>.Instance);
            Store = new(source, NullLogger<RunStore>.Instance, lease);
            recovery = new(source, Store, lease, server.Clock, NullLogger<RunRecovery>.Instance);
            Scheduler = new(source, lease, recovery, server.Versions, new(server.Clock), server.Clock,
                NullLogger<ScheduleFireService>.Instance, new("prod"));
        }

        public async Task Start()
        {
            await lease.StartAsync(Token);
            await WaitFor(() => Task.FromResult(lease.IsLeader), "leader lease");
            // Startup recovery gates the real scheduler; repeated run-recovery sweeps are not under test.
            await recovery.RecoverAsync(Token);
            await Scheduler.StartAsync(Token);
        }

        public async Task Stop()
        {
            if (stopped) return;
            try { await Scheduler.StopAsync(Token); }
            finally
            {
                try { await recovery.StopAsync(Token); }
                finally { await lease.StopAsync(Token); }
            }
            stopped = true;
        }

        public async ValueTask DisposeAsync()
        {
            await Stop();
            Scheduler.Dispose();
            recovery.Dispose();
            lease.Dispose();
        }
    }
}
