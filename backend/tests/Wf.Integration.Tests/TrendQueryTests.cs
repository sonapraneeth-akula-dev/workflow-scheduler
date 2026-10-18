using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Npgsql;
using Wf.ControlPlane.Api.Series;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed class TrendQueryTests(RollupDatabase database, ITestOutputHelper output) : IClassFixture<RollupDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Today = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Today.AddHours(12);
    }
    private sealed record Scope(Guid Series, Guid Schedule, string Workflow, Guid Group);

    [Theory]
    [InlineData("7d", 7, 6, 3, 40, 92.5)]
    [InlineData("30d", 30, 7, 3, 50, 107)]
    [InlineData("90d", 90, 8, 4, 60, 175)]
    public async Task Exact_window_counts_quantiles_gaps_and_creation_order_streak_match_seeded_answers(
        string window, int days, long runs, long successes, double p50, double p95)
    {
        var scope = await Registry();
        await Seed(scope, Today.AddDays(1 - days).AddTicks(-10), 3, 999);
        await Seed(scope, Today.AddDays(-6), 3, 10);
        await Seed(scope, Today.AddDays(-5), 4, 20);
        await Seed(scope, Today.AddDays(-5).AddHours(1), 5, 30);
        await Seed(scope, Today.AddDays(-2), 6, 100);
        await Seed(scope, Today.AddDays(-1), 3, 50);
        await Seed(scope, Today.AddHours(1), 3, 70);
        await Seed(scope, Today.AddHours(2), 1, 999);
        await Seed(scope, Today.AddDays(1), 4, 999);
        await Seed(scope, new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 4, 110);
        await Seed(scope, new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), 3, 210);
        var foreign = await Registry();
        await Seed(foreign, Today, 3, 999999);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var response = await Read(api, scope, window);
        Assert.Equal(scope.Series, response.SeriesId);
        Assert.Equal(window, response.Window);
        Assert.Equal(DateOnly.FromDateTime(Today.Date).AddDays(1), response.To);
        Assert.Equal(response.To.AddDays(-days), response.From);
        Assert.Equal(days, response.Days.Length);
        Assert.Equal((runs, successes, runs - successes, runs),
            (response.Totals.Runs, response.Totals.Succeeded, response.Totals.Failed, response.Totals.DurationCount));
        Assert.Equal((double)successes / runs, response.Totals.SuccessRate);
        Assert.Equal(p50, response.Totals.P50Ms!.Value, 6);
        Assert.Equal(p95, response.Totals.P95Ms!.Value, 6);
        Assert.Equal(new("Succeeded", 2), response.Streak);
        var sameDay = Assert.Single(response.Days, day => day.Day == new DateOnly(2026, 10, 12)).Metrics;
        Assert.Equal((2L, 0L, 2L, 2L), (sameDay.Runs, sameDay.Succeeded, sameDay.Failed, sameDay.DurationCount));
        Assert.Equal(25, sameDay.P50Ms);
        Assert.Equal(29.5, sameDay.P95Ms);
        var gap = Assert.Single(response.Days, day => day.Day == new DateOnly(2026, 10, 13)).Metrics;
        Assert.Equal(new(0, 0, 0, 0, null, null, null), gap);
        Assert.Equal(response.Totals.Runs, response.Days.Sum(day => day.Metrics.Runs));
    }

    [Fact]
    public async Task Empty_and_deleted_schedule_series_have_null_measurements_not_invented_success_or_streak()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var empty = await Read(api, scope, "90d");
        Assert.Equal(new(0, 0, 0, 0, null, null, null), empty.Totals);
        Assert.Equal(new(null, 0), empty.Streak);
        Assert.Equal(90, empty.Days.Length);
        Assert.All(empty.Days, day => Assert.Equal(empty.Totals, day.Metrics));
        await Execute("UPDATE schedules SET deleted_at=now(),paused=true,next_fire_at=NULL WHERE id=$1", scope.Schedule);
        Assert.Equal(JsonSerializer.Serialize(empty, SeriesTrendsJson.Default.SeriesTrendsResponse),
            JsonSerializer.Serialize(await Read(api, scope, "90d"), SeriesTrendsJson.Default.SeriesTrendsResponse));
        var legacy = scope with { Series = Guid.NewGuid() };
        await Seed(legacy, Today, 3, null);
        var retained = await Read(api, legacy);
        Assert.Equal((1L, 1L, 0L), (retained.Totals.Runs, retained.Totals.Succeeded, retained.Totals.DurationCount));
        Assert.Null(retained.Totals.P50Ms);
        Assert.Null(retained.Totals.P95Ms);
        Assert.Equal(new("Succeeded", 1), retained.Streak);
    }

    [Fact]
    public async Task Failure_streak_combines_failure_abort_and_timeout_and_ignores_active_runs()
    {
        var scope = await Registry();
        foreach (var (offset, status) in new (int, short)[] { (-5,3), (-4,4), (-3,5), (-2,6), (-1,2) })
            await Seed(scope, Today.AddDays(offset), status, offset == -4 ? -20 : 10);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var response = await Read(api, scope, "7d");
        Assert.Equal(new("Failed", 3), response.Streak);
        Assert.Equal((4L, 1L, 3L, 4L), (response.Totals.Runs, response.Totals.Succeeded,
            response.Totals.Failed, response.Totals.DurationCount));
        Assert.Equal(10, response.Totals.P50Ms);
        Assert.Equal(0, Assert.Single(response.Days, day => day.Day == DateOnly.FromDateTime(Today.AddDays(-4).Date))
            .Metrics.P50Ms);
    }

    [Fact]
    public async Task Viewer_scope_is_current_exact_and_retained_without_requiring_the_SPA_client()
    {
        var scope = await Registry();
        var foreign = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var jwt = api.Jwt(scope.Group);
        using (var allowed = await api.Get(Path(scope.Series), jwt, Token))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using (var anonymous = await api.Get(Path(scope.Series), null, Token))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var id in new[] { foreign.Series, Guid.NewGuid() })
        {
            using var denied = await api.Get(Path(id), jwt, Token);
            await Problem(denied, 403, "insufficient_scope_role");
        }
        using (var app = await api.Get(Path(scope.Series), api.Jwt(scope.Group, identity: "app"), Token))
            await Problem(app, 403, "insufficient_scope_role");
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", scope.Group);
        using (var revoked = await api.Get(Path(scope.Series), jwt, Token))
            await Problem(revoked, 403, "insufficient_scope_role");
        using (var admin = await api.Get(Path(scope.Series), api.Jwt(role: "Admin"), Token))
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Theory]
    [InlineData("?window=1d")]
    [InlineData("?window=")]
    [InlineData("?window=7d&window=90d")]
    [InlineData("?window=7d,30d,90d")]
    [InlineData("?window=7D")]
    [InlineData("?Window=7d")]
    [InlineData("?window=7d&team=other")]
    [InlineData("?limit=1")]
    public async Task Invalid_queries_fail_explicitly_instead_of_defaulting_or_returning_HTML(string suffix)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        using var response = await api.Get(Path(scope.Series) + suffix, api.Jwt(scope.Group), Token);
        await Problem(response, 400, "invalid_trend_window");
    }

    [Fact]
    public async Task Invalid_identity_or_encoded_route_fails_and_storage_errors_are_sanitized()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        var jwt = api.Jwt(scope.Group);
        foreach (var id in new[] { "not-a-guid", Guid.Empty.ToString(), scope.Series.ToString("N") })
        {
            using var invalid = await api.Get(Path(id), jwt, Token);
            await Problem(invalid, 400, "invalid_series_identity");
        }
        using (var encoded = await api.Get(Path(scope.Series).Replace("/trends", "/%74rends"), jwt, Token))
            await Problem(encoded, 400, "invalid_series_identity");
        await Execute("ALTER TABLE runs RENAME COLUMN ended_at TO unavailable_trend_end");
        try
        {
            using var unavailable = await api.Get(Path(scope.Series), jwt, Token);
            await Problem(unavailable, 503, "series_trends_unavailable");
            var text = await unavailable.Content.ReadAsStringAsync(Token);
            foreach (var detail in new[] { "Npgsql", "unavailable_trend_end", "SELECT", "password" })
                Assert.DoesNotContain(detail, text, StringComparison.OrdinalIgnoreCase);
        }
        finally { await Execute("ALTER TABLE runs RENAME COLUMN unavailable_trend_end TO ended_at"); }
    }

    [Fact]
    public async Task Ninety_days_at_two_thousand_runs_per_day_have_known_answers_and_HTTP_p95_under_500ms()
    {
        var scope = await Registry();
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        {
            await using (var partitions = new NpgsqlCommand("""
                SELECT ensure_run_partitions(day::date) FROM generate_series($1::timestamptz,$2::timestamptz,interval '1 month') day;
                """, connection))
            {
                partitions.Parameters.AddWithValue(Today.AddDays(-89).AddDays(-20));
                partitions.Parameters.AddWithValue(Today.AddMonths(1));
                await partitions.ExecuteNonQueryAsync(Token);
            }
            await using var seed = new NpgsqlCommand("""
                WITH input AS (
                    SELECT n,$3::timestamptz+(n/2000)*interval '1 day'+(n%2000)*interval '1 second' AS at,
                        md5($1::text||':'||n::text) AS suffix FROM generate_series(0,179999) n
                )
                INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,
                    status,created_at,started_at,ended_at,deadline_at)
                SELECT (at AT TIME ZONE 'UTC')::date,
                    (lpad(to_hex((extract(epoch FROM at)*1000)::bigint),12,'0')||'7'||substr(suffix,1,3)||'8'||substr(suffix,4,15))::uuid,
                    $2,$1,$1,0,'trend-performance','{}','{}',
                    CASE WHEN n%5=4 THEN 4 ELSE 3 END::smallint,at,at,at+(n%100+1)*interval '1 second',at+interval '1 day'
                FROM input
                """, connection);
            seed.Parameters.AddWithValue(scope.Series);
            seed.Parameters.AddWithValue(scope.Workflow);
            seed.Parameters.AddWithValue(Today.AddDays(-89));
            seed.CommandTimeout = 120;
            Assert.Equal(180000, await seed.ExecuteNonQueryAsync(Token));
            await using var analyze = new NpgsqlCommand("ANALYZE runs", connection);
            await analyze.ExecuteNonQueryAsync(Token);
        }
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: new Clock());
        for (var warmup = 0; warmup < 3; warmup++) await Read(api, scope, "90d");
        var timings = new List<double>();
        for (var sample = 0; sample < 20; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            var response = await Read(api, scope, "90d");
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal((180000L, 144000L, 36000L, 180000L), (response.Totals.Runs, response.Totals.Succeeded,
                response.Totals.Failed, response.Totals.DurationCount));
            Assert.Equal(0.8, response.Totals.SuccessRate);
            Assert.Equal(50500, response.Totals.P50Ms!.Value, 5);
            Assert.Equal(95050, response.Totals.P95Ms!.Value, 5);
            Assert.Equal(new("Failed", 1), response.Streak);
            Assert.Equal(90, response.Days.Length);
            Assert.All(response.Days, day => Assert.Equal(2000, day.Metrics.Runs));
        }
        var p95 = timings.Order().ElementAt((int)Math.Ceiling(timings.Count * 0.95) - 1);
        output.WriteLine($"Real signed-JWT HTTP /series/trends, 180000 rows, 90 days, 20 samples: p95={p95:F2}ms.");
        Assert.True(p95 < 500, $"Trend HTTP p95 {p95:F2}ms must be below 500ms.");
    }

    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Trends_browser_real_Postgres_signed_Viewer_charts_windows_tables_empty_denial_and_accessibility()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_TRENDS_BROWSER") == "true",
            "Set WF_TRENDS_BROWSER=true and WF_FRONTEND_ROOT with installed browser dependencies.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(System.IO.Path.Combine(frontend, "scripts", "trends-browser.ts")));
        var scope = await Registry();
        var empty = await Registry(scope.Group);
        for (var day = 0; day < 90; day++)
            await Seed(scope, Today.AddDays(day - 89), (short)(day % 5 == 4 ? 4 : 3), (day + 1) * 1000);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, browser: true, timeProvider: new Clock());
        var viewer = api.Jwt(scope.Group);
        var foreign = api.Jwt(Guid.NewGuid());
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(System.IO.Path.Combine("scripts", "trends-browser.ts"));
        start.Environment["WF_TRENDS_API"] = api.Api;
        start.Environment["WF_TRENDS_FIXTURE"] = JsonSerializer.Serialize(new
        {
            seriesId = scope.Series, emptySeriesId = empty.Series, viewerToken = viewer, foreignToken = foreign
        });
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Trends browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        var logs = await stdout + await stderr;
        Assert.DoesNotContain(viewer, logs);
        Assert.DoesNotContain(foreign, logs);
        output.WriteLine(logs);
    }

    private async Task<Scope> Registry(Guid? group = null)
    {
        var team = "trends-" + Guid.NewGuid().ToString("N");
        var scope = new Scope(Guid.CreateVersion7(), Guid.CreateVersion7(), $"{team}/insights/workflow", group ?? Guid.NewGuid());
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team, [new(team, "insights", "Trend integration", new([]))],
                RoleBindings: [new(Contracts.Registry.RegistryRole.Viewer, scope.Group)]), Token);
        await using var seed = new NpgsqlCommand("""
            WITH saved AS (
                INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
                VALUES($1,$2,1,'Trends','UTC',60,'trend-test','trend-test') RETURNING workflow_id,id
            )
            INSERT INTO run_series(id,workflow_id,schedule_id) SELECT $3,workflow_id,id FROM saved
            """, connection);
        seed.Parameters.AddWithValue(scope.Schedule);
        seed.Parameters.AddWithValue(scope.Workflow);
        seed.Parameters.AddWithValue(scope.Series);
        await seed.ExecuteNonQueryAsync(Token);
        await transaction.CommitAsync(Token);
        return scope;
    }

    private async Task Seed(Scope scope, DateTimeOffset created, short status, double? duration)
    {
        var id = Guid.CreateVersion7(created);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using (var partitions = new NpgsqlCommand("SELECT ensure_run_partitions(uuid_v7_run_day($1))", connection))
        {
            partitions.Parameters.AddWithValue(id);
            await partitions.ExecuteNonQueryAsync(Token);
        }
        await using var seed = new NpgsqlCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,
                status,created_at,started_at,ended_at,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$1,$3,0,'private-trigger','{"secret":"private-variable"}',
                '{"private":"private-metadata"}',$4,$5,$6,$7,$5+interval '1 day')
            """, connection);
        seed.Parameters.AddWithValue(id);
        seed.Parameters.AddWithValue(scope.Workflow);
        seed.Parameters.AddWithValue(scope.Series);
        seed.Parameters.AddWithValue(status);
        seed.Parameters.AddWithValue(created);
        seed.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = duration is null ? DBNull.Value : created });
        seed.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = status < 3 ? DBNull.Value : created.AddMilliseconds(duration ?? 1) });
        await seed.ExecuteNonQueryAsync(Token);
    }

    private async Task<SeriesTrendsResponse> Read(MetricsApiServer api, Scope scope, string? window = null)
    {
        using var response = await api.Get(Path(scope.Series) + (window is null ? "" : "?window=" + window),
            api.Jwt(scope.Group), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var text = await response.Content.ReadAsStringAsync(Token);
        foreach (var detail in new[] { "private-trigger", "private-variable", "private-metadata", "outputs", "packageUri" })
            Assert.DoesNotContain(detail, text);
        return JsonSerializer.Deserialize(text, SeriesTrendsJson.Default.SeriesTrendsResponse)!;
    }

    private static string Path(object id) => $"/api/v1/series/{id}/trends";
    private async Task Execute(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) query.Parameters.AddWithValue(parameter);
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
