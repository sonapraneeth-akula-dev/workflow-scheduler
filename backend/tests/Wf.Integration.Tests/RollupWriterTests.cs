using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Maintenance;
using Wf.Engine.Core;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests;

public sealed class RollupWriterTests(RollupDatabase database) : IClassFixture<RollupDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly RunStore store = new(database.DataSource, NullLogger<RunStore>.Instance);
    private static string Workflow() => "rollups/" + Guid.NewGuid().ToString("N");
    private static readonly DateTimeOffset Epoch = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Seeded_counts_exact_quantiles_soft_failures_and_retry_resource_maxima_are_idempotent()
    {
        var workflow = Workflow();
        var ids = new List<Guid>();
        foreach (var (status, nodeStatus, duration) in new (short, short, double?)[]
                 { (3,5,10), (3,6,20), (4,7,100), (5,9,null), (6,8,40) })
            ids.Add(await Seed(database.DataSource, workflow, Epoch, status, nodeStatus, duration));
        await Seed(database.DataSource, workflow, Epoch, 1, 3, 100000);
        await Seed(database.DataSource, Workflow(), Epoch, 3, 5, 999999);
        await Seed(database.DataSource, workflow, Epoch.AddDays(-1), 4, 7, 999999);
        await Resources(ids[0], "alpha/task", 1, 120, 64 * 1048576);
        await Resources(ids[0], "alpha/task", 2, 30, 16 * 1048576);
        await Refresh(ids.ToArray());
        await Refresh(ids.ToArray());
        var rows = await Rows(workflow);
        var run = Assert.Single(rows, row => row.Day == DateOnly.FromDateTime(Epoch.Date) && row.Node == "");
        Assert.Equal((5L, 3L, 1L, 4L), (run.Runs, run.Failures, run.Soft, run.Durations));
        Assert.Equal(30000, run.P50!.Value, 6);
        Assert.Equal(91000, run.P95!.Value, 6);
        Assert.Equal(120, run.Cpu);
        Assert.Equal(64, run.Ram);
        var node = Assert.Single(rows, row => row.Node == "alpha");
        Assert.Equal((5L, 3L, 1L, 4L), (node.Runs, node.Failures, node.Soft, node.Durations));
        Assert.Equal(15000, node.P50!.Value, 6);
        Assert.Equal(45500, node.P95!.Value, 6);
        Assert.Equal(120, node.Cpu);
        Assert.Equal(64, node.Ram);
        var skipped = Assert.Single(rows, row => row.Node == "skipped");
        Assert.Equal((5L, 0L, 0L, 0L), (skipped.Runs, skipped.Failures, skipped.Soft, skipped.Durations));
        Assert.Null(skipped.P50);
        Assert.Null(skipped.P95);
        Assert.Null(skipped.Cpu);
        Assert.Null(skipped.Ram);
        Assert.Equal(3, rows.Count);
        var dashboard = Assert.Single(await DashboardRows(workflow), row => row.Node == "alpha");
        Assert.Equal((5L, 3L, 1L), (dashboard.Observations, dashboard.Failures, dashboard.Soft));
        Assert.Equal([5000d, 10000d, 20000d, 50000d], dashboard.Durations.Order());
        Assert.Equal(120, dashboard.Cpu);
        Assert.Equal(64, dashboard.Ram);
        Assert.Contains(dashboard.Latest, ids);
        var dashboardSkipped = Assert.Single(await DashboardRows(workflow), row => row.Node == "skipped");
        Assert.Empty(dashboardSkipped.Durations);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Aborted)]
    [InlineData(RunStatus.TimedOut)]
    public async Task Actual_run_store_terminal_transaction_updates_workflow_and_node_rollups(RunStatus status)
    {
        var workflow = Workflow();
        var run = await Create(workflow);
        var events = Finish(run, status, 12);
        foreach (var change in events) await store.AppendAsync(run, change, Token);
        var rows = await Rows(workflow);
        Assert.Equal(2, rows.Count);
        var total = Assert.Single(rows, row => row.Node == "");
        Assert.Equal(1, total.Runs);
        Assert.Equal(status == RunStatus.Succeeded ? 0 : 1, total.Failures);
        Assert.Equal(12000, total.P50);
        Assert.Equal(12000, total.P95);
        var node = Assert.Single(rows, row => row.Node == "alpha");
        Assert.Equal(6000, node.P50);
        Assert.Null(total.Cpu);
        Assert.Null(total.Ram);
        var dashboard = Assert.Single(await DashboardRows(workflow));
        Assert.Equal((1L, status == RunStatus.Succeeded ? 0L : 1L), (dashboard.Observations, dashboard.Failures));
        Assert.Equal([6000d], dashboard.Durations);
        Assert.Equal(run.Snapshot.RunId, dashboard.Latest);
    }

    [Fact]
    public async Task Concurrent_terminal_commits_do_not_lose_counts_or_average_percentiles()
    {
        var workflow = Workflow();
        var runs = new List<RunState>();
        for (var index = 0; index < 24; index++) runs.Add(await Create(workflow));
        await Task.WhenAll(runs.Select(async (run, index) =>
        {
            foreach (var change in Finish(run, index % 3 == 0 ? RunStatus.Failed : RunStatus.Succeeded, index + 1))
                await store.AppendAsync(run, change, Token);
        }));
        var row = Assert.Single(await Rows(workflow), r => r.Node == "");
        Assert.Equal((24L, 8L, 24L), (row.Runs, row.Failures, row.Durations));
        Assert.Equal(12500, row.P50!.Value, 6);
        Assert.Equal(22850, row.P95!.Value, 6);
        var dashboard = Assert.Single(await DashboardRows(workflow));
        Assert.Equal((24L, 8L), (dashboard.Observations, dashboard.Failures));
        Assert.Equal(Enumerable.Range(1, 24).Select(value => value * 500d), dashboard.Durations.Order());
    }

    [Fact]
    public async Task Group_commit_projects_terminal_timestamps_nodes_and_rollups_once_even_on_exact_replay()
    {
        var workflow = Workflow();
        var first = await Create(workflow);
        var second = await Create(workflow);
        var changes = Finish(first, RunStatus.Succeeded, 10).Concat(Finish(second, RunStatus.Failed, 30)).ToArray();
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        await appender.AppendAsync(changes, Token);
        await appender.AppendAsync(changes, Token);
        var rows = await Rows(workflow);
        var total = Assert.Single(rows, row => row.Node == "");
        Assert.Equal((2L, 1L, 2L), (total.Runs, total.Failures, total.Durations));
        Assert.Equal(20000, total.P50);
        Assert.Equal(29000, total.P95);
        Assert.Equal(10000, Assert.Single(rows, row => row.Node == "alpha").P50);
        var dashboard = Assert.Single(await DashboardRows(workflow));
        Assert.Equal((2L, 1L), (dashboard.Observations, dashboard.Failures));
        Assert.Equal([5000d, 15000d], dashboard.Durations.Order());
        await using var query = database.DataSource.CreateCommand("""
            SELECT r.started_at,r.ended_at,n.started_at,n.ended_at,r.last_seq=r.snapshot_seq
            FROM runs r JOIN node_runs n ON n.run_day=r.run_day AND n.run_id=r.id
            WHERE r.id=$1 AND n.node_key='alpha'
            """);
        query.Parameters.AddWithValue(first.Snapshot.RunId);
        await using var reader = await query.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(Epoch, reader.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(Epoch.AddSeconds(10), reader.GetFieldValue<DateTimeOffset>(1));
        Assert.Equal(Epoch, reader.GetFieldValue<DateTimeOffset>(2));
        Assert.Equal(Epoch.AddSeconds(5), reader.GetFieldValue<DateTimeOffset>(3));
        Assert.True(reader.GetBoolean(4));
    }

    [Theory]
    [InlineData(false, "daily_rollups")]
    [InlineData(true, "daily_rollups")]
    [InlineData(false, "dashboard_rollups")]
    [InlineData(true, "dashboard_rollups")]
    public async Task Rollup_failure_rolls_back_terminal_events_snapshots_and_projections_before_acknowledgement(bool grouped, string table)
    {
        var workflow = Workflow();
        var run = await Create(workflow);
        var changes = Finish(run, RunStatus.Succeeded, 10);
        if (!grouped)
            foreach (var change in changes[..^1]) await store.AppendAsync(run, change, Token);
        var before = (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot;
        var name = "reject_rollup_" + Guid.NewGuid().ToString("N");
        await using (var install = database.DataSource.CreateCommand($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected rollup failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW
                WHEN (NEW.workflow_id='{workflow}') EXECUTE FUNCTION {name}();
            """))
            await install.ExecuteNonQueryAsync(Token);
        try
        {
            await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
            var error = await Assert.ThrowsAsync<PostgresException>(() => grouped
                ? appender.AppendAsync(changes, Token)
                : store.AppendAsync(run, changes[^1], Token));
            Assert.Equal("P0001", error.SqlState);
            var saved = (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot;
            Assert.Equal(before.LastSequence, saved.LastSequence);
            Assert.Equal(before.Status, saved.Status);
            Assert.Equal(before.EndedAt, saved.EndedAt);
            Assert.Empty(await Rows(workflow));
            Assert.Empty(await DashboardRows(workflow));
        }
        finally
        {
            await using var cleanup = database.DataSource.CreateCommand($"""
                DROP TRIGGER {name} ON {table}; DROP FUNCTION {name}();
                """);
            await cleanup.ExecuteNonQueryAsync(Token);
        }
        if (grouped)
        {
            await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
            await appender.AppendAsync(changes, Token);
        }
        else await store.AppendAsync(run, changes[^1], Token);
        Assert.Equal(1, Assert.Single(await Rows(workflow), row => row.Node == "").Runs);
        Assert.Equal(1, Assert.Single(await DashboardRows(workflow)).Observations);
    }

    [Fact]
    public async Task Previous_schema_upgrade_backfills_retained_terminal_history_without_changing_runs()
    {
        var schema = "rollup_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = await database.DataSource.OpenConnectionAsync(Token);
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync(Token);
        try
        {
            await using var previous = database.ForSchema(schema);
            foreach (var name in new[] { "0002_runs_events.sql", "0003_attempt_timers.sql", "0025_resource_samples.sql" })
            {
                await using var migration = previous.CreateCommand(await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "Migrations", name), Token));
                await migration.ExecuteNonQueryAsync(Token);
            }
            var workflow = Workflow();
            await Seed(previous, workflow, Epoch.AddDays(-1), 3, 5, 10);
            await Seed(previous, workflow, Epoch.AddDays(-1), 4, 7, 30);
            await Seed(previous, workflow, Epoch, 3, 5, 80);
            await Seed(previous, workflow, Epoch, 0, 0, null);
            await using (var upgrade = previous.CreateCommand(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Migrations", "0036_daily_rollups.sql"), Token)))
                await upgrade.ExecuteNonQueryAsync(Token);
            var rows = await Rows(workflow, previous);
            Assert.Equal(6, rows.Count);
            var prior = Assert.Single(rows, row => row.Node == "" && row.Day == DateOnly.FromDateTime(Epoch.AddDays(-1).Date));
            Assert.Equal((2L, 1L, 2L), (prior.Runs, prior.Failures, prior.Durations));
            Assert.Equal(20000, prior.P50);
            Assert.Equal(29000, prior.P95);
            await using (var caller = await previous.OpenConnectionAsync(Token))
            {
                await using var refresh = new NpgsqlCommand("SELECT refresh_daily_rollups($1,$2)", caller);
                refresh.Parameters.AddWithValue(DateOnly.FromDateTime(Epoch.Date));
                refresh.Parameters.AddWithValue(workflow);
                await refresh.PrepareAsync(Token);
                await using (var upgrade = previous.CreateCommand(await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "Migrations", "0037_dashboard_rollups.sql"), Token)))
                    await upgrade.ExecuteNonQueryAsync(Token);
                var dashboards = await DashboardRows(workflow, previous);
                Assert.Equal(4, dashboards.Count);
                var historical = Assert.Single(dashboards,
                    row => row.Node == "alpha" && row.Day == DateOnly.FromDateTime(Epoch.AddDays(-1).Date));
                Assert.Equal((2L, 1L), (historical.Observations, historical.Failures));
                Assert.Equal([5000d, 15000d], historical.Durations.Order());
                await Seed(previous, workflow, Epoch, 4, 7, 100);
                await refresh.ExecuteNonQueryAsync(Token);
                var updated = Assert.Single(await DashboardRows(workflow, previous),
                    row => row.Node == "alpha" && row.Day == DateOnly.FromDateTime(Epoch.Date));
                Assert.Equal((2L, 1L), (updated.Observations, updated.Failures));
                Assert.Equal([40000d, 50000d], updated.Durations.Order());
            }
            await using var query = previous.CreateCommand("SELECT count(*),sum(last_seq) FROM runs");
            await using var reader = await query.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(5, reader.GetInt64(0));
            Assert.Equal(0, reader.GetDecimal(1));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync(Token);
        }
    }

    [Fact]
    public async Task Clock_skew_is_clamped_but_missing_start_is_not_fabricated_as_zero()
    {
        var workflow = Workflow();
        var first = await Seed(database.DataSource, workflow, Epoch, 4, 7, -20);
        var second = await Seed(database.DataSource, workflow, Epoch, 5, 9, null);
        await Refresh([first, second]);
        var total = Assert.Single(await Rows(workflow), row => row.Node == "");
        Assert.Equal(2, total.Runs);
        Assert.Equal(1, total.Durations);
        Assert.Equal(0, total.P50);
        Assert.Equal(0, total.P95);
    }

    [Theory]
    [InlineData("failures=runs+1")]
    [InlineData("soft_failures=-1")]
    [InlineData("duration_count=runs+1")]
    [InlineData("p50_ms=NULL")]
    [InlineData("p95_ms=p50_ms-1")]
    [InlineData("cpu_max_pct='NaN'::float8")]
    [InlineData("ram_peak_mb=-1")]
    public async Task Persisted_rollups_reject_inconsistent_counts_and_nonfinite_or_fabricated_measurements(string mutation)
    {
        var workflow = Workflow();
        var id = await Seed(database.DataSource, workflow, Epoch, 3, 5, 10);
        await Refresh([id]);
        await using var invalid = database.DataSource.CreateCommand(
            $"UPDATE daily_rollups SET {mutation} WHERE workflow_id=$1 AND node_key=''");
        invalid.Parameters.AddWithValue(workflow);
        var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Theory]
    [InlineData("failures=observations+1")]
    [InlineData("soft_failures=observations")]
    [InlineData("duration_count=observations+1")]
    [InlineData("durations_ms='{}'::float8[]")]
    [InlineData("durations_ms=ARRAY[NULL::float8]")]
    [InlineData("durations_ms=ARRAY['NaN'::float8]")]
    [InlineData("durations_ms=ARRAY['Infinity'::float8]")]
    [InlineData("durations_ms=ARRAY[-1::float8]")]
    [InlineData("cpu_max_pct='NaN'::float8")]
    [InlineData("ram_peak_mb=-1")]
    public async Task Dashboard_projection_rejects_invalid_counts_arrays_and_nonfinite_measurements(string mutation)
    {
        var workflow = Workflow();
        var id = await Seed(database.DataSource, workflow, Epoch, 4, 7, 10);
        await Refresh([id]);
        await using var invalid = database.DataSource.CreateCommand(
            $"UPDATE dashboard_rollups SET {mutation} WHERE workflow_id=$1 AND node_key='alpha'");
        invalid.Parameters.AddWithValue(workflow);
        var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private async Task<RunState> Create(string workflow)
    {
        var run = new RunState(Guid.CreateVersion7(), [new("alpha", [], [new("task")])],
            new WorkflowMetadata("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, workflow, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
        return run;
    }

    private static RunEvent[] Finish(RunState run, RunStatus status, double seconds)
    {
        var copy = RunState.Restore(run.Snapshot, [], Assert.Fail);
        var nodeStatus = status switch
        {
            RunStatus.Succeeded => ExecutionStatus.Succeeded,
            RunStatus.Aborted => ExecutionStatus.Cancelled,
            RunStatus.TimedOut => ExecutionStatus.TimedOut,
            _ => ExecutionStatus.Failed
        };
        var events = new List<RunEvent> { copy.Append(EventEntity.Run, null, (int)RunStatus.Running, Epoch) };
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            events.Add(copy.Append(EventEntity.Node, "alpha", (int)state, Epoch));
        events.Add(copy.Append(EventEntity.Node, "alpha", (int)nodeStatus, Epoch.AddSeconds(seconds / 2)));
        if (status == RunStatus.Aborted)
            events.Add(copy.Append(EventEntity.Run, null, (int)RunStatus.Aborting, Epoch.AddSeconds(seconds / 2)));
        events.Add(copy.Append(EventEntity.Run, null, (int)status, Epoch.AddSeconds(seconds)));
        return events.ToArray();
    }

    private async Task<Guid> Seed(NpgsqlDataSource source, string workflow, DateTimeOffset at, short status,
        short nodeStatus, double? seconds)
    {
        var id = Guid.CreateVersion7(at);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using (var partition = new NpgsqlCommand("SELECT ensure_run_partitions(uuid_v7_run_day($1))", connection))
        {
            partition.Parameters.AddWithValue(id);
            await partition.ExecuteNonQueryAsync(Token);
        }
        await using (var seed = new NpgsqlCommand("""
            WITH saved AS (
                INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,
                    created_at,started_at,ended_at,deadline_at)
                VALUES(uuid_v7_run_day($1),$1,$2,$1,0,'rollup-test','{}','{}',$3,$4,$5,$6,$4+interval '1 day')
                RETURNING run_day,id
            )
            INSERT INTO node_runs(run_day,run_id,node_key,status,started_at,ended_at)
            SELECT s.run_day,s.id,n.* FROM saved s CROSS JOIN (VALUES
                ('alpha',$7,$5,$8),('skipped',10::smallint,NULL::timestamptz,$6)
            ) AS n(node_key,status,started_at,ended_at)
            """, connection))
        {
            seed.Parameters.AddWithValue(id);
            seed.Parameters.AddWithValue(workflow);
            seed.Parameters.AddWithValue(status);
            seed.Parameters.AddWithValue(at);
            seed.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
                Value = seconds is null ? DBNull.Value : at });
            seed.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
                Value = seconds is { } duration ? at.AddSeconds(duration) : at.AddSeconds(1) });
            seed.Parameters.AddWithValue(nodeStatus);
            seed.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
                Value = seconds is { } nodeDuration ? at.AddSeconds(nodeDuration / 2) : at.AddSeconds(1) });
            await seed.ExecuteNonQueryAsync(Token);
        }
        await transaction.CommitAsync(Token);
        return id;
    }

    private async Task Resources(Guid id, string key, short attempt, double cpu, decimal ram)
    {
        await using var query = database.DataSource.CreateCommand("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,
                resource_max_cpu_percent,resource_peak_ram_bytes)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,5,now(),$4,$5)
            """);
        query.Parameters.AddWithValue(id);
        query.Parameters.AddWithValue(key);
        query.Parameters.AddWithValue(attempt);
        query.Parameters.AddWithValue(cpu);
        query.Parameters.AddWithValue(ram);
        await query.ExecuteNonQueryAsync(Token);
    }

    private async Task Refresh(Guid[] ids)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await RollupWriter.RefreshAsync(connection, ids, Token);
        await transaction.CommitAsync(Token);
    }

    private sealed record Row(DateOnly Day, string Node, long Runs, long Failures, long Soft, long Durations,
        double? P50, double? P95, double? Cpu, decimal? Ram);

    private async Task<List<Row>> Rows(string workflow, NpgsqlDataSource? source = null)
    {
        await using var query = (source ?? database.DataSource).CreateCommand("""
            SELECT day,node_key,runs,failures,soft_failures,duration_count,p50_ms,p95_ms,cpu_max_pct,ram_peak_mb
            FROM daily_rollups WHERE workflow_id=$1 ORDER BY day,node_key
            """);
        query.Parameters.AddWithValue(workflow);
        await using var reader = await query.ExecuteReaderAsync(Token);
        var rows = new List<Row>();
        while (await reader.ReadAsync(Token))
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7), reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9)));
        return rows;
    }

    private sealed record DashboardProjectionRow(DateOnly Day, string Node, long Observations, long Failures, long Soft,
        double[] Durations, double? Cpu, decimal? Ram, Guid Latest);

    private async Task<List<DashboardProjectionRow>> DashboardRows(string workflow, NpgsqlDataSource? source = null)
    {
        await using var query = (source ?? database.DataSource).CreateCommand("""
            SELECT day,node_key,observations,failures,soft_failures,durations_ms,cpu_max_pct,ram_peak_mb,latest_run_id
            FROM dashboard_rollups WHERE scope='workflow' AND workflow_id=$1 ORDER BY day,node_key
            """);
        query.Parameters.AddWithValue(workflow);
        await using var reader = await query.ExecuteReaderAsync(Token);
        var rows = new List<DashboardProjectionRow>();
        while (await reader.ReadAsync(Token))
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetFieldValue<double[]>(5), reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.GetGuid(8)));
        return rows;
    }
}

public sealed class RollupDatabase : AgentDatabase
{
    public string ConnectionString => DatabaseConnectionString;
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
    public NpgsqlDataSource SingleConnection() => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(DatabaseConnectionString) { MaxPoolSize = 1 }.ConnectionString);
    public NpgsqlDataSource ForSchema(string schema) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(DatabaseConnectionString) { SearchPath = schema }.ConnectionString);
}
