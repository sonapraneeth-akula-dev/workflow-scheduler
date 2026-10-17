using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.ControlPlane.Api.Dashboards;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed class DashboardEndpointTests(RollupDatabase database) : IClassFixture<RollupDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Today = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Today.AddHours(12); }
    private sealed record Scope(string Workflow, Guid Schedule, Guid Series, Guid Group, Guid Machine);

    [Fact]
    public async Task All_four_scopes_return_exact_rankings_and_contributing_run_links_without_cross_machine_attribution()
    {
        var scope = await Registry();
        var old = await Seed(scope, Today.AddDays(-1), 3, 5, 10, 100, 32);
        var latest = await Seed(scope, Today, 4, 7, 100, 200, 64);
        await Seed(scope, Today.AddDays(-35), 3, 5, 9999, 9999, 9999);
        await Seed(scope, Today.AddHours(1), 1, 3, 9999, 9999, 9999);
        await Seed(scope, Today.AddDays(1), 4, 7, 9999, 9999, 9999);
        var foreign = await Registry(machine: scope.Machine);
        await Seed(foreign, Today, 4, 7, 999999, 999999, 999999);
        await Execute("""
            WITH changed AS (
                UPDATE task_attempts SET started_at=started_at+interval '1 second',ended_at=started_at+interval '3 seconds'
                WHERE run_id=$1 AND task_key='alpha/task' RETURNING run_id
            )
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,agent_id,
                started_at,ended_at,resource_max_cpu_percent,resource_peak_ram_bytes)
            VALUES(uuid_v7_run_day($1),$1,'alpha/task',2,5,now()+interval '1 day',$2,now(),now()+interval '500 seconds',900,900*1048576)
            """, latest, Guid.NewGuid());
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        foreach (var (kind, id) in new[]
        {
            ("workflow", DashboardQuery.Encode(scope.Workflow)),
            ("schedule", scope.Schedule.ToString()),
            ("node", DashboardQuery.Encode(scope.Workflow + "/alpha")),
            ("machine", scope.Machine.ToString())
        })
        {
            var response = await Read(api, scope, kind, id);
            Assert.Equal((kind, id, "30d", 10, kind == "machine" ? "TaskAttempt" : "Node"),
                (response.Scope, response.Id, response.Window, response.Top, response.ObservationKind));
            var row = Assert.Single(response.Rankings.Slowest);
            Assert.Equal(scope.Workflow, row.WorkflowId);
            Assert.Equal("alpha", row.NodeKey);
            Assert.Equal((2L, 1L, 0L, 2L), (row.Observations, row.Failures, row.SoftFailures, row.DurationCount));
            Assert.Equal(0.5, row.FailureRate);
            Assert.Equal(kind == "machine" ? 6000 : 55000, row.P50Ms);
            Assert.Equal(kind == "machine" ? 9600 : 95500, row.P95Ms);
            Assert.Equal(kind == "machine" ? 200 : 900, row.CpuMaxPercent);
            Assert.Equal(kind == "machine" ? 64 : 900, row.RamPeakMb);
            Assert.Equal(latest, row.LatestRunId);
            Assert.Equal($"/runs/{latest:D}", row.RunHref);
            Assert.Equal(row, Assert.Single(response.Rankings.Cpu));
            Assert.Equal(row, Assert.Single(response.Rankings.Ram));
        }
        Assert.NotEqual(old, latest);
    }

    [Fact]
    public async Task Unknown_values_skipped_soft_failure_and_zero_duration_are_not_fabricated_or_excluded()
    {
        var scope = await Registry();
        await Seed(scope, Today, 3, 6, null, null, null);
        var zero = await Seed(scope, Today.AddHours(1), 3, 5, 0, 0, 0);
        await Execute("""
            INSERT INTO node_runs(run_day,run_id,node_key,status)
                VALUES(uuid_v7_run_day($1),$1,'skipped',10)
            """, zero);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var response = await Read(api, scope, "workflow", DashboardQuery.Encode(scope.Workflow));
        var measured = Assert.Single(response.Rankings.Slowest);
        Assert.Equal((2L, 0L, 1L, 1L), (measured.Observations, measured.Failures, measured.SoftFailures, measured.DurationCount));
        Assert.Equal(0, measured.P95Ms);
        Assert.Equal(0, Assert.Single(response.Rankings.Cpu).CpuMaxPercent);
        Assert.Equal(0, Assert.Single(response.Rankings.Ram).RamPeakMb);
        var skipped = Assert.Single(response.Rankings.FailureRate, row => row.NodeKey == "skipped");
        Assert.Null(skipped.P95Ms);
        Assert.Null(skipped.CpuMaxPercent);
        Assert.Null(skipped.RamPeakMb);
    }

    [Fact]
    public async Task Authorization_is_fresh_exact_and_retained_for_deleted_schedules()
    {
        var scope = await Registry();
        await Seed(scope, Today, 3, 5, 1, null, null);
        var foreign = await Registry();
        await Seed(foreign, Today, 3, 5, 999, null, null);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        using (var anonymous = await api.Get(Path("workflow", DashboardQuery.Encode(scope.Workflow)), null, Token))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var (kind, id) in new[]
        {
            ("workflow", DashboardQuery.Encode(foreign.Workflow)), ("schedule", foreign.Schedule.ToString()),
            ("node", DashboardQuery.Encode(foreign.Workflow + "/alpha")), ("machine", foreign.Machine.ToString()),
            ("workflow", DashboardQuery.Encode(scope.Workflow + "-missing")), ("schedule", Guid.NewGuid().ToString()),
            ("node", DashboardQuery.Encode(scope.Workflow + "/missing")), ("machine", Guid.NewGuid().ToString())
        })
        {
            using var denied = await api.Get(Path(kind, id), api.Jwt(scope.Group), Token);
            await Problem(denied, 403, "insufficient_scope_role");
        }
        await Execute("UPDATE schedules SET deleted_at=now(),paused=true,next_fire_at=NULL WHERE id=$1", scope.Schedule);
        Assert.Single((await Read(api, scope, "schedule", scope.Schedule.ToString())).Rankings.Slowest);
        using (var app = await api.Get(Path("workflow", DashboardQuery.Encode(scope.Workflow)), api.Jwt(scope.Group, identity: "app"), Token))
            await Problem(app, 403, "insufficient_scope_role");
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", scope.Group);
        foreach (var (kind, id) in new[]
        {
            ("workflow", DashboardQuery.Encode(scope.Workflow)), ("schedule", scope.Schedule.ToString()),
            ("node", DashboardQuery.Encode(scope.Workflow + "/alpha")), ("machine", scope.Machine.ToString())
        })
        {
            using var revoked = await api.Get(Path(kind, id), api.Jwt(scope.Group), Token);
            await Problem(revoked, 403, "insufficient_scope_role");
            using var admin = await api.Get(Path(kind, id), api.Jwt(role: "Admin"), Token);
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        }
    }

    [Theory]
    [InlineData("?window=7D")]
    [InlineData("?window=7d&window=90d")]
    [InlineData("?top=0")]
    [InlineData("?top=51")]
    [InlineData("?top=01")]
    [InlineData("?top=1&top=2")]
    [InlineData("?metadata=private")]
    public async Task Invalid_filters_fail_explicitly(string suffix)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        using var invalid = await api.Get(Path("workflow", DashboardQuery.Encode(scope.Workflow)) + suffix, api.Jwt(scope.Group), Token);
        await Problem(invalid, 400, "invalid_dashboard_query");
    }

    [Fact]
    public async Task Empty_windows_return_empty_lists_and_storage_failures_are_sanitized()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var empty = await Read(api, scope, "workflow", DashboardQuery.Encode(scope.Workflow));
        Assert.Empty(empty.Rankings.Slowest);
        Assert.Empty(empty.Rankings.FailureRate);
        await Execute("ALTER TABLE node_runs RENAME COLUMN ended_at TO unavailable_dashboard_end");
        try
        {
            using var unavailable = await api.Get(Path("workflow", DashboardQuery.Encode(scope.Workflow)), api.Jwt(scope.Group), Token);
            await Problem(unavailable, 503, "dashboard_unavailable");
            var body = await unavailable.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("unavailable_dashboard_end", body);
            Assert.DoesNotContain("Npgsql", body);
        }
        finally { await Execute("ALTER TABLE node_runs RENAME COLUMN unavailable_dashboard_end TO ended_at"); }
        using var encoded = await api.Get(Path("%77orkflow", DashboardQuery.Encode(scope.Workflow)), api.Jwt(scope.Group), Token);
        await Problem(encoded, 400, "invalid_dashboard_query");
    }

    private async Task<Scope> Registry(Guid? machine = null)
    {
        var team = "dashboard-" + Guid.NewGuid().ToString("N");
        var scope = new Scope($"{team}/insights/workflow", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.NewGuid(), machine ?? Guid.NewGuid());
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction, new(team,
            [new(team, "insights", "Dashboard integration", new([]))],
            RoleBindings: [new(Contracts.Registry.RegistryRole.Viewer, scope.Group)]), Token);
        await using var query = new NpgsqlCommand("""
            WITH saved AS (
                INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
                VALUES($1,$2,1,'Dashboard','UTC',60,'test','test') RETURNING id,workflow_id
            ), series AS (
                INSERT INTO run_series(id,workflow_id,schedule_id) SELECT $3,workflow_id,id FROM saved RETURNING id
            )
            INSERT INTO agents(id,name,os,arch,cores,ram_mb,max_tasks,labels,pool,status,last_heartbeat_at,agent_version,session_id)
            VALUES($4,$4::text,1,0,4,1024,4,'{}','default',0,now(),'test',$4) ON CONFLICT DO NOTHING
            """, connection, transaction);
        query.Parameters.AddWithValue(scope.Schedule);
        query.Parameters.AddWithValue(scope.Workflow);
        query.Parameters.AddWithValue(scope.Series);
        query.Parameters.AddWithValue(scope.Machine);
        await query.ExecuteNonQueryAsync(Token);
        await transaction.CommitAsync(Token);
        return scope;
    }

    private async Task<Guid> Seed(Scope scope, DateTimeOffset at, short status, short nodeStatus,
        double? duration, double? cpu, decimal? ram)
    {
        var id = Guid.CreateVersion7(at);
        await Execute("SELECT ensure_run_partitions(uuid_v7_run_day($1))", id);
        await Execute("""
            WITH saved AS (
                INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,
                    status,created_at,started_at,ended_at,deadline_at)
                VALUES(uuid_v7_run_day($1),$1,$2,$1,$3,0,'private-trigger','{"secret":"private-variable"}',
                    '{"private":"private-metadata"}',$4,$5,$6,$7,$5+interval '1 day') RETURNING id
            ), nodes AS (
                INSERT INTO node_runs(run_day,run_id,node_key,status,started_at,ended_at)
                VALUES(uuid_v7_run_day($1),$1,'alpha',$8,$6,$7) RETURNING run_id
            )
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,agent_id,started_at,ended_at,
                resource_max_cpu_percent,resource_peak_ram_bytes)
            VALUES(uuid_v7_run_day($1),$1,'alpha/task',1,$8,$5+interval '1 day',$9,$6,$7,$10,$11)
            """, id, scope.Workflow, scope.Series, status, at,
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = duration is null ? DBNull.Value : at },
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = duration is null ? DBNull.Value : at.AddSeconds(duration.Value) },
            nodeStatus, scope.Machine, new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)cpu ?? DBNull.Value },
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = ram is null ? DBNull.Value : ram * 1048576 });
        return id;
    }

    private async Task<DashboardResponse> Read(MetricsApiServer api, Scope scope, string kind, string id)
    {
        using var response = await api.Get(Path(kind, id), api.Jwt(scope.Group), Token);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        foreach (var secret in new[] { "private-trigger", "private-variable", "private-metadata", "packageUri", "outputs" })
            Assert.DoesNotContain(secret, body);
        return JsonSerializer.Deserialize(body, DashboardJson.Default.DashboardResponse)!;
    }

    private static string Path(string kind, string id) => $"/api/v1/dashboards/{kind}/{id}";
    private async Task Execute(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            if (parameter is NpgsqlParameter typed) query.Parameters.Add(typed);
            else query.Parameters.AddWithValue(parameter);
        await query.ExecuteNonQueryAsync(Token);
    }
    private async Task Problem(HttpResponseMessage response, int status, string reason)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(reason, json.RootElement.GetProperty("reason").GetString());
    }
}
