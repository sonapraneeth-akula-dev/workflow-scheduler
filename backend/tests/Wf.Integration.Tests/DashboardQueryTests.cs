using System.Diagnostics;
using Npgsql;
using Wf.ControlPlane.Api.Dashboards;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed partial class DashboardEndpointTests
{
    [Fact]
    public async Task Workflow_trends_cover_180000_runs_even_without_node_observations()
    {
        var scope = await Registry();
        await Execute("SELECT ensure_run_partitions($1)", DateOnly.FromDateTime(Today.UtcDateTime));
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,
                status,created_at,started_at,ended_at,deadline_at)
            SELECT $1,
                (lpad(to_hex((extract(epoch FROM $4::timestamptz)*1000)::bigint+n),12,'0')||
                    '70008000000000000000')::uuid,$2,$3,$3,0,'trends-performance','{}','{}',
                CASE WHEN n%5=4 THEN 4 ELSE 3 END::smallint,$4,$4,$4+(n%100+1)*interval '1 second',$4+interval '1 day'
            FROM generate_series(0,179999) n
            """);
        seed.Parameters.AddWithValue(DateOnly.FromDateTime(Today.UtcDateTime));
        seed.Parameters.AddWithValue(scope.Workflow);
        seed.Parameters.AddWithValue(scope.Series);
        seed.Parameters.AddWithValue(Today);
        seed.CommandTimeout = 180;
        await seed.ExecuteNonQueryAsync(Token);
        await Execute("ANALYZE runs");
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        for (var i = 0; i < 3; i++) await Read(api, scope, "workflow", DashboardQuery.Encode(scope.Workflow), "?window=90d");
        var timings = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var response = await Read(api, scope, "workflow", DashboardQuery.Encode(scope.Workflow), "?window=90d");
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Empty(response.Rankings.Slowest);
            Assert.Equal(180000, response.Trends!.Total.Runs);
            Assert.Equal(50500, response.Trends.Total.P50Ms!.Value, 5);
            Assert.Equal(90100, response.Trends.Total.P90Ms!.Value, 5);
            Assert.Equal(99010, response.Trends.Total.P99Ms!.Value, 5);
            Assert.Equal(100000, response.Trends.Total.P999Ms!.Value, 5);
        }
        output.WriteLine($"Workflow trends only: max={timings.Max():F2}ms, median={timings.Order().ElementAt(5):F2}ms");
    }

    [Fact]
    public async Task Multi_node_rankings_windows_top_ties_nulls_and_schedule_isolation_match_known_answers()
    {
        var scope = await Registry();
        var older = await Seed(scope, Today.AddDays(-8), 3, 5, 1000, 1000, 1000);
        var first = await Seed(scope, Today.AddDays(-1), 3, 5, 10, 20, 30);
        var second = await Seed(scope, Today, 4, 7, 30, 60, 50);
        await Execute("""
            INSERT INTO node_runs(run_day,run_id,node_key,status,started_at,ended_at)
            SELECT run_day,run_id,key,status,started_at,
                CASE WHEN key='unmeasured' THEN NULL ELSE started_at+interval '5 seconds' END
            FROM node_runs CROSS JOIN (VALUES('beta'),('gamma'),('unmeasured')) AS extra(key)
            WHERE run_id=ANY($1) AND node_key='alpha'
            """, new[] { first, second });
        await Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,started_at,ended_at,deadline_at,
                resource_max_cpu_percent,resource_peak_ram_bytes)
            SELECT run_day,run_id,key||'/task',attempt,agent_id,7,started_at,started_at+interval '5 seconds',deadline_at,
                CASE WHEN key='unmeasured' THEN NULL ELSE 200 END,
                CASE WHEN key='unmeasured' THEN NULL ELSE 300*1048576 END
            FROM task_attempts CROSS JOIN (VALUES('beta'),('gamma'),('unmeasured')) AS extra(key)
            WHERE run_id=ANY($1) AND task_key='alpha/task'
            """, new[] { first, second });
        await Execute("UPDATE node_runs SET status=7 WHERE run_id=ANY($1) AND node_key IN ('beta','gamma')",
            new[] { first, second });
        var otherSchedule = scope with { Schedule = Guid.NewGuid(), Series = Guid.NewGuid() };
        await Seed(otherSchedule, Today, 3, 5, 10000, 10000, 10000);
        await Refresh(scope);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var selected = await Read(api, scope, "schedule", scope.Schedule.ToString(), "?window=7d&top=2");
        Assert.Equal(["alpha", "beta"], selected.Rankings.Slowest.Select(row => row.NodeKey));
        Assert.Equal(["beta", "gamma"], selected.Rankings.FailureRate.Select(row => row.NodeKey));
        Assert.Equal(["beta", "gamma"], selected.Rankings.Cpu.Select(row => row.NodeKey));
        Assert.Equal(["beta", "gamma"], selected.Rankings.Ram.Select(row => row.NodeKey));
        var alpha = selected.Rankings.Slowest[0];
        Assert.Equal((2L, 1L, 2L), (alpha.Observations, alpha.Failures, alpha.DurationCount));
        Assert.Equal(20000, alpha.P50Ms);
        Assert.Equal(29000, alpha.P95Ms);
        Assert.Equal(second, alpha.LatestRunId);
        var previous = await Read(api, scope, "schedule", scope.Schedule.ToString(), "?window=30d");
        var acrossDays = previous.Rankings.Slowest[0];
        Assert.Equal(30000, acrossDays.P50Ms);
        Assert.Equal(903000, acrossDays.P95Ms!.Value, 5);
        Assert.Equal(1000, acrossDays.CpuMaxPercent);
        Assert.Equal(1000, acrossDays.RamPeakMb);
        Assert.Equal(3, acrossDays.Observations);
        var unknown = Assert.Single(previous.Rankings.FailureRate, row => row.NodeKey == "unmeasured");
        Assert.Null(unknown.P95Ms);
        Assert.DoesNotContain(previous.Rankings.Slowest, row => row.NodeKey == "unmeasured");
        Assert.DoesNotContain(previous.Rankings.Cpu, row => row.NodeKey == "unmeasured");
        var workflow = await Read(api, scope, "workflow", DashboardQuery.Encode(scope.Workflow), "?window=7d");
        Assert.Equal(3, workflow.Rankings.Slowest[0].Observations);
        Assert.Equal(10000, workflow.Rankings.Cpu[0].CpuMaxPercent);
        Assert.NotEqual(older, acrossDays.LatestRunId);
    }

    [Fact]
    public async Task All_dashboard_scopes_have_exact_90_day_2000_daily_run_rankings_and_HTTP_p95_under_500ms()
    {
        var scope = await Registry();
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        {
            await using (var partitions = new NpgsqlCommand("""
                SELECT ensure_run_partitions(day::date)
                FROM generate_series($1::timestamptz,$2::timestamptz,interval '1 month') day
                """, connection))
            {
                partitions.Parameters.AddWithValue(Today.AddDays(-110));
                partitions.Parameters.AddWithValue(Today.AddMonths(1));
                await partitions.ExecuteNonQueryAsync(Token);
            }
            await using (var seed = new NpgsqlCommand("""
                WITH input AS (
                    SELECT n,$3::timestamptz+(n/2000)*interval '1 day'+(n%2000)*interval '1 second' AS at,
                        md5($1::text||':'||n::text) AS suffix FROM generate_series(0,179999) n
                )
                INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,
                    status,created_at,started_at,ended_at,deadline_at)
                SELECT (at AT TIME ZONE 'UTC')::date,
                    (lpad(to_hex((extract(epoch FROM at)*1000)::bigint),12,'0')||'7'||substr(suffix,1,3)||'8'||substr(suffix,4,15))::uuid,
                    $2,$1,$1,0,'dashboard-performance','{}','{}',
                    CASE WHEN n%5=4 THEN 4 ELSE 3 END::smallint,at,at,at+(n%100+1)*interval '1 second',at+interval '1 day'
                FROM input
                """, connection))
            {
                seed.Parameters.AddWithValue(scope.Series);
                seed.Parameters.AddWithValue(scope.Workflow);
                seed.Parameters.AddWithValue(Today.AddDays(-89));
                seed.CommandTimeout = 180;
                Assert.Equal(180000, await seed.ExecuteNonQueryAsync(Token));
            }
            await using (var analyze = new NpgsqlCommand("ANALYZE runs", connection))
                await analyze.ExecuteNonQueryAsync(Token);
            await using (var nodes = new NpgsqlCommand("""
                INSERT INTO node_runs(run_day,run_id,node_key,status,started_at,ended_at)
                SELECT run_day,id,key,CASE WHEN key='alpha' AND status=4 THEN 7 ELSE 5 END,
                    started_at,CASE WHEN key='alpha' THEN ended_at ELSE started_at+interval '10 seconds' END
                FROM runs CROSS JOIN (VALUES('alpha'),('beta')) AS extra(key) WHERE workflow_id=$1
                """, connection))
            {
                nodes.Parameters.AddWithValue(scope.Workflow);
                nodes.CommandTimeout = 600;
                Assert.Equal(360000, await nodes.ExecuteNonQueryAsync(Token));
            }
            await using (var analyze = new NpgsqlCommand("ANALYZE node_runs", connection))
                await analyze.ExecuteNonQueryAsync(Token);
            await using (var attempts = new NpgsqlCommand("""
                INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,started_at,ended_at,deadline_at,
                    resource_max_cpu_percent,resource_peak_ram_bytes)
                SELECT n.run_day,n.run_id,n.node_key||'/task',1,$2,n.status,n.started_at,n.ended_at,n.started_at+interval '1 day',
                    CASE WHEN n.node_key='alpha' THEN 50 ELSE 150 END,
                    CASE WHEN n.node_key='alpha' THEN 128*1048576 ELSE 512*1048576 END
                FROM node_runs n JOIN runs r ON r.id=n.run_id WHERE r.workflow_id=$1
                """, connection))
            {
                attempts.Parameters.AddWithValue(scope.Workflow);
                attempts.Parameters.AddWithValue(scope.Machine);
                attempts.CommandTimeout = 600;
                Assert.Equal(360000, await attempts.ExecuteNonQueryAsync(Token));
            }
            foreach (var table in new[] { "runs", "node_runs", "task_attempts" })
            {
                await using var analyze = new NpgsqlCommand("ANALYZE " + table, connection);
                await analyze.ExecuteNonQueryAsync(Token);
            }
            await using var rollups = new NpgsqlCommand("""
                SELECT refresh_daily_rollups(run_day,workflow_id)
                FROM runs WHERE workflow_id=$1 GROUP BY run_day,workflow_id ORDER BY run_day
                """, connection);
            rollups.Parameters.AddWithValue(scope.Workflow);
            rollups.CommandTimeout = 600;
            await rollups.ExecuteNonQueryAsync(Token);
        }
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var measured = new Dictionary<string, double>();
        foreach (var (kind, id) in new[]
        {
            ("workflow", DashboardQuery.Encode(scope.Workflow)), ("schedule", scope.Schedule.ToString()),
            ("node", DashboardQuery.Encode(scope.Workflow + "/alpha")), ("machine", scope.Machine.ToString())
        })
        {
            for (var warmup = 0; warmup < 3; warmup++) await Read(api, scope, kind, id, "?window=90d");
            var timings = new List<double>();
            for (var sample = 0; sample < 20; sample++)
            {
                var started = Stopwatch.GetTimestamp();
                var response = await Read(api, scope, kind, id, "?window=90d");
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Assert.Equal(kind == "machine" ? "TaskAttempt" : "Node", response.ObservationKind);
                Assert.Equal(["alpha"], response.Rankings.Slowest.Take(1).Select(row => row.NodeKey));
                var alpha = response.Rankings.Slowest[0];
                Assert.Equal((180000L, 36000L, 0L, 180000L),
                    (alpha.Observations, alpha.Failures, alpha.SoftFailures, alpha.DurationCount));
                Assert.Equal(0.2, alpha.FailureRate);
                Assert.Equal(50500, alpha.P50Ms!.Value, 5);
                Assert.Equal(95050, alpha.P95Ms!.Value, 5);
                Assert.Equal(50, alpha.CpuMaxPercent);
                Assert.Equal(128, alpha.RamPeakMb);
                Assert.Equal(alpha, response.Rankings.FailureRate[0]);
                Assert.StartsWith("/runs/", alpha.RunHref);
                if (kind == "workflow")
                {
                    var trends = Assert.IsType<WorkflowTrends>(response.Trends);
                    Assert.Equal((180000L, 180000L, 180000L),
                        (trends.Total.Runs, trends.Total.Completed, trends.Total.DurationCount));
                    Assert.Equal(50500, trends.Total.P50Ms!.Value, 5);
                    Assert.Equal(90100, trends.Total.P90Ms!.Value, 5);
                    Assert.Equal(99010, trends.Total.P99Ms!.Value, 5);
                    Assert.Equal(100000, trends.Total.P999Ms!.Value, 5);
                    Assert.Equal(90, trends.Days.Length);
                    Assert.All(trends.Days, day => Assert.Equal(2000, day.Metrics.Runs));
                }
                if (kind == "node")
                {
                    Assert.Single(response.Rankings.Cpu);
                    Assert.Single(response.Rankings.Ram);
                }
                else
                {
                    var beta = response.Rankings.Cpu[0];
                    Assert.Equal("beta", beta.NodeKey);
                    Assert.Equal(150, beta.CpuMaxPercent);
                    Assert.Equal(512, beta.RamPeakMb);
                    Assert.Equal(10000, beta.P95Ms);
                    Assert.Equal(0, beta.FailureRate);
                    Assert.Equal(180000, beta.Observations);
                    Assert.Equal(beta, response.Rankings.Ram[0]);
                }
            }
            var p95 = timings.Order().ElementAt((int)Math.Ceiling(timings.Count * 0.95) - 1);
            output.WriteLine($"Actual signed-JWT HTTP dashboard {kind}, 180000 runs/360000 nodes/360000 attempts, 90 days, 20 samples: p95={p95:F2}ms.");
            measured.Add(kind, p95);
        }
        Assert.All(measured, item => Assert.True(item.Value < 500,
            $"{item.Key} dashboard HTTP p95 {item.Value:F2}ms must be below 500ms."));
    }

    [Fact]
    public async Task Persisted_source_quantiles_match_PostgreSQL_native_interpolation_for_null_zero_skew_duplicate_and_distinct_durations()
    {
        var scope = await Registry();
        var ids = new List<Guid>();
        for (var index = 0; index < 97; index++)
        {
            double? duration = index % 11 == 0 ? null : index % 13 == 0 ? -1 :
                index % 7 == 0 ? 0 : index % 3 == 0 ? 0.123456 : index * index * 0.000001;
            ids.Add(await Seed(scope, Today.AddMinutes(index), 3, (short)(5 + index % 7), duration,
                index % 5 == 0 ? null : index, index % 5 == 0 ? null : index));
        }
        double[] expected;
        await using (var query = database.DataSource.CreateCommand("""
            SELECT percentile_cont(ARRAY[0.5,0.95]) WITHIN GROUP (
                ORDER BY greatest(0,date_part('epoch',ended_at-started_at)*1000))
            FROM node_runs WHERE run_id=ANY($1) AND started_at IS NOT NULL AND ended_at IS NOT NULL
            """))
        {
            query.Parameters.AddWithValue(ids.ToArray());
            expected = (double[])(await query.ExecuteScalarAsync(Token))!;
        }
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        foreach (var (kind, id) in new[]
        {
            ("workflow", DashboardQuery.Encode(scope.Workflow)), ("schedule", scope.Schedule.ToString()),
            ("node", DashboardQuery.Encode(scope.Workflow + "/alpha")), ("machine", scope.Machine.ToString())
        })
        {
            var response = await Read(api, scope, kind, id, "?window=7d");
            var row = Assert.Single(response.Rankings.Slowest);
            Assert.Equal(expected[0], row.P50Ms!.Value, 9);
            Assert.Equal(expected[1], row.P95Ms!.Value, 9);
            Assert.Equal(97, row.Observations);
            Assert.Equal(42, row.Failures);
            Assert.Equal(14, row.SoftFailures);
            Assert.Equal(88, row.DurationCount);
            Assert.Equal(96, row.CpuMaxPercent);
            Assert.Equal(96, row.RamPeakMb);
            if (kind == "workflow")
            {
                await using var oracle = database.DataSource.CreateCommand("""
                    SELECT percentile_cont(ARRAY[0.5,0.9,0.99,0.999]) WITHIN GROUP (
                        ORDER BY greatest(0,date_part('epoch',ended_at-started_at)*1000))
                    FROM runs WHERE id=ANY($1) AND started_at IS NOT NULL AND ended_at IS NOT NULL
                    """);
                oracle.Parameters.AddWithValue(ids.ToArray());
                var quantiles = (double[])(await oracle.ExecuteScalarAsync(Token))!;
                var actual = response.Trends!.Total;
                Assert.Equal(quantiles[0], actual.P50Ms!.Value, 9);
                Assert.Equal(quantiles[1], actual.P90Ms!.Value, 9);
                Assert.Equal(quantiles[2], actual.P99Ms!.Value, 9);
                Assert.Equal(quantiles[3], actual.P999Ms!.Value, 9);
            }
        }
    }

    [Fact]
    public async Task Pooled_connection_remains_usable_with_unchanged_settings_after_success_and_storage_failure()
    {
        var scope = await Registry();
        await Seed(scope, Today, 3, 5, 1, 1, 1);
        await using var source = database.SingleConnection();
        var before = await Settings();
        await using var api = await MetricsApiServer.Start(source, Token, timeProvider: new Clock());
        Assert.Single((await Read(api, scope, "workflow", DashboardQuery.Encode(scope.Workflow))).Rankings.Slowest);
        Assert.Equal(before, await Settings());
        await Execute("ALTER TABLE dashboard_rollups RENAME COLUMN durations_ms TO unavailable_dashboard_durations");
        try
        {
            using var unavailable = await api.Get(Path("workflow", DashboardQuery.Encode(scope.Workflow)), api.Jwt(scope.Group), Token);
            await Problem(unavailable, 503, "dashboard_unavailable");
            Assert.Equal(before, await Settings());
        }
        finally { await Execute("ALTER TABLE dashboard_rollups RENAME COLUMN unavailable_dashboard_durations TO durations_ms"); }

        async Task<string> Settings()
        {
            await using var query = source.CreateCommand("""
                SELECT pg_backend_pid(),current_setting('jit'),current_setting('work_mem'),
                    current_setting('max_parallel_workers_per_gather'),current_setting('min_parallel_table_scan_size'),
                    current_setting('min_parallel_index_scan_size')
                """);
            await using var reader = await query.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            return string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue));
        }
    }
}
