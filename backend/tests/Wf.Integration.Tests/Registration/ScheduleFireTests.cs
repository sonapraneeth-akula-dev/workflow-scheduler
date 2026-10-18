using System.Diagnostics;
using System.Net;
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

public sealed partial class ScheduleFireTests(RegistrationDatabase database) : IClassFixture<RegistrationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ScheduleFire_concurrent_claims_from_two_engines_create_one_complete_run_and_advance_once()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var leader = new Engine(database.DataSource, server);
        await using var standby = new Engine(database.DataSource, server);
        Assert.True(await leader.Lease.TryAcquireOrRenewAsync(Token));
        Assert.False(await standby.Lease.TryAcquireOrRenewAsync(Token));
        await Assert.ThrowsAsync<NotLeaderException>(() => standby.Scheduler.FireAsync(schedule.Timer, Token));
        var timers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => leader.Scheduler.FireAsync(schedule.Timer, Token)));
        Assert.All(timers, timer => Assert.Equal(schedule.Timer.FireAt.AddMinutes(1), timer!.FireAt));
        var run = Assert.IsType<Guid>(await Scalar("SELECT run_id FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal((short)0, await Scalar("SELECT outcome FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal((short)1, await Scalar("SELECT trigger FROM runs WHERE id=$1", run));
        Assert.Equal("scheduler", await Scalar("SELECT triggered_by FROM runs WHERE id=$1", run));
        Assert.Equal(schedule.Version, await Scalar("SELECT version_id FROM runs WHERE id=$1", run));
        Assert.Equal(schedule.Timer.FireAt.AddMinutes(1), await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        Assert.Equal(1, await Scalar("SELECT row_version FROM schedules WHERE id=$1", schedule.Id));
        var stored = await leader.Store.LoadAsync(run, Token);
        Assert.Equal("first", stored.Snapshot.Variables["env"].GetString());
        Assert.Equal("release", stored.Snapshot.Metadata.Type);
        Assert.Single(stored.Snapshot.Nodes);
        Assert.Single(stored.Snapshot.Tasks);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM run_snapshots WHERE run_id=$1", run));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM node_runs WHERE run_id=$1", run));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1", run));
        await leader.Recovery.RecoverAsync(Token);
        Assert.True(leader.Recovery.RecoveredRuns.ContainsKey(run));
    }

    [Theory]
    [InlineData("runs")]
    [InlineData("schedules")]
    public async Task ScheduleFire_failed_run_insert_or_cursor_update_rolls_back_claim_run_snapshot_nodes_and_cursor(string table)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var function = "reject_fire_" + Guid.NewGuid().ToString("N");
        await Execute($$"""
            CREATE FUNCTION {{function}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.workflow_id='{{schedule.Workflow}}' THEN RAISE EXCEPTION 'injected fire rollback'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {{function}} BEFORE {{(table == "runs" ? "INSERT" : "UPDATE")}} ON {{table}}
                FOR EACH ROW EXECUTE FUNCTION {{function}}()
            """);
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => engine.Scheduler.FireAsync(schedule.Timer, Token));
            Assert.Contains("injected fire rollback", error.Message);
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE workflow_id=$1", schedule.Workflow));
            Assert.Equal(0L, await Scalar("""
                SELECT count(*) FROM run_snapshots s LEFT JOIN runs r ON r.run_day=s.run_day AND r.id=s.run_id WHERE r.id IS NULL
                """));
            Assert.Equal(0L, await Scalar("""
                SELECT count(*) FROM node_runs n LEFT JOIN runs r ON r.run_day=n.run_day AND r.id=n.run_id WHERE r.id IS NULL
                """));
            Assert.Equal(schedule.Timer.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        }
        finally { await Execute($"DROP TRIGGER {function} ON {table}; DROP FUNCTION {function}()"); }
        Assert.NotNull(await engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
    }

    [Fact]
    public async Task ScheduleFire_edits_apply_at_the_next_fire_and_accepted_run_keeps_its_previous_snapshot()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server, overlap: "Concurrent");
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var next = (await engine.Scheduler.FireAsync(schedule.Timer, Token))!;
        var original = Assert.IsType<Guid>(await Scalar("SELECT run_id FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        using var update = await server.Post(schedule.Request with { Manifest = WithVersion(schedule.Request, "1.1.0") });
        Assert.Equal(HttpStatusCode.Created, update.StatusCode);
        var latest = (await Json(update)).GetProperty("workflowVersion").GetGuid();
        await Execute("""
            UPDATE schedules SET interval_s=120,variables='{"env":"edited"}',version_pin='1.*',row_version=row_version+1 WHERE id=$1
            """, schedule.Id);
        server.Clock.Now = next.FireAt;
        var following = (await engine.Scheduler.FireAsync(next, Token))!;
        Assert.Equal(next.FireAt.AddMinutes(2), following.FireAt);
        var second = Assert.IsType<Guid>(await Scalar(
            "SELECT run_id FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2", schedule.Id, next.FireAt));
        Assert.Equal(latest, await Scalar("SELECT version_id FROM runs WHERE id=$1", second));
        Assert.Equal(schedule.Version, await Scalar("SELECT version_id FROM runs WHERE id=$1", original));
        Assert.Equal("edited", (await engine.Store.LoadAsync(second, Token)).Snapshot.Variables["env"].GetString());
        Assert.Equal("first", (await engine.Store.LoadAsync(original, Token)).Snapshot.Variables["env"].GetString());
        Assert.Equal(schedule.Series, await Scalar("SELECT series_id FROM runs WHERE id=$1", second));
    }

    [Fact]
    public async Task ScheduleFire_reads_the_current_active_pointer_inside_the_fire_transaction_not_a_stale_cache_snapshot()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        using var registration = await server.Post(schedule.Request with { Manifest = WithVersion(schedule.Request, "1.1.0") });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var latest = (await Json(registration)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => server.Versions.GetActive(schedule.Workflow).VersionId == latest);
        await Execute("UPDATE workflows SET active_version_id=$2 WHERE id=$1", schedule.Workflow, schedule.Version);
        Assert.Equal(latest, server.Versions.GetActive(schedule.Workflow).VersionId);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await engine.Scheduler.FireAsync(schedule.Timer, Token);
        Assert.Equal(schedule.Version, await Scalar("SELECT version_id FROM runs WHERE series_id=$1", schedule.Series));
    }

    [Fact]
    public async Task ScheduleFire_persisted_cron_uses_its_IANA_timezone_and_skips_the_duplicate_fixed_time_in_a_fall_fold()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        var fire = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        await Execute("""
            UPDATE schedules SET cron='30 1 * * *',interval_s=NULL,timezone='America/New_York',next_fire_at=$2 WHERE id=$1
            """, schedule.Id, fire);
        server.Clock.Now = fire;
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var next = (await engine.Scheduler.FireAsync(new(schedule.Id, fire), Token))!;
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), next.FireAt);
        Assert.Equal(next.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
    }

    [Fact]
    public async Task ScheduleFire_cancelled_transaction_releases_its_claim_and_can_be_retried()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var function = "block_fire_" + Guid.NewGuid().ToString("N");
        var key = Random.Shared.Next(1, int.MaxValue);
        await Execute($$"""
            CREATE FUNCTION {{function}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.workflow_id='{{schedule.Workflow}}' THEN PERFORM pg_advisory_xact_lock({{key}}); END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {{function}} BEFORE INSERT ON runs FOR EACH ROW EXECUTE FUNCTION {{function}}()
            """);
        try
        {
            await using var blocker = await database.DataSource.OpenConnectionAsync(Token);
            await using var transaction = await blocker.BeginTransactionAsync(Token);
            await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", blocker, transaction))
            {
                hold.Parameters.AddWithValue((long)key);
                await hold.ExecuteNonQueryAsync(Token);
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var firing = engine.Scheduler.FireAsync(schedule.Timer, cancellation.Token);
            await WaitFor(async () => (bool)(await Scalar("""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND classid=0 AND objid::bigint=$1)
                """, (long)key))!);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firing);
            await transaction.RollbackAsync(Token);
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
            Assert.Equal(schedule.Timer.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        }
        finally { await Execute($"DROP TRIGGER {function} ON runs; DROP FUNCTION {function}()"); }
        Assert.NotNull(await engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
    }

    [Theory]
    [InlineData("paused=true")]
    [InlineData("deleted_at=now()")]
    [InlineData("next_fire_at=NULL")]
    public async Task ScheduleFire_rechecks_paused_deleted_and_exhausted_rows_instead_of_firing_stale_heap_entries(string edit)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await Execute($"UPDATE schedules SET {edit} WHERE id=$1", schedule.Id);
        Assert.Null(await engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
    }

    [Fact]
    public async Task ScheduleFire_future_and_retimed_entries_are_rearmed_without_using_an_obsolete_fire_slot()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var future = schedule.Timer.FireAt.AddHours(1);
        await Execute("UPDATE schedules SET next_fire_at=$2 WHERE id=$1", schedule.Id, future);
        Assert.Equal(future, (await engine.Scheduler.FireAsync(schedule.Timer, Token))!.FireAt);
        Assert.Equal(future, (await engine.Scheduler.FireAsync(new(schedule.Id, future), Token))!.FireAt);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
    }

    [Theory]
    [InlineData("version_pin='9.*'")]
    [InlineData("version_pin='^1.0.0'")]
    [InlineData("variables='{\"unknown\":\"must-not-leak\"}'")]
    public async Task ScheduleFire_invalid_pin_or_values_never_commit_a_partial_claim_or_advance_the_cursor(string edit)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await Execute($"UPDATE schedules SET {edit} WHERE id=$1", schedule.Id);
        var error = await Assert.ThrowsAsync<ScheduleFireRejectedException>(() => engine.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.DoesNotContain("must-not-leak", error.Message);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(schedule.Timer.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        await Execute("UPDATE schedules SET version_pin=NULL,variables='{\"env\":\"repaired\"}' WHERE id=$1", schedule.Id);
        Assert.NotNull(await engine.Scheduler.FireAsync(schedule.Timer, Token));
    }

    [Theory]
    [InlineData("Skip", 1L, (short)1)]
    [InlineData("Queue", 2L, (short)0)]
    [InlineData("Concurrent", 2L, (short)0)]
    public async Task ScheduleFire_overlap_is_decided_with_the_claim_under_the_schedule_lock(string overlap, long runs, short outcome)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server, overlap);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var next = (await engine.Scheduler.FireAsync(schedule.Timer, Token))!;
        server.Clock.Now = next.FireAt;
        await engine.Scheduler.FireAsync(next, Token);
        Assert.Equal(runs, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(outcome, await Scalar("SELECT outcome FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2", schedule.Id, next.FireAt));
        if (overlap == "Skip")
        {
            Assert.Equal("overlap", await Scalar("SELECT reason FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2", schedule.Id, next.FireAt));
            Assert.Equal(DBNull.Value, await Scalar("SELECT run_id FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2", schedule.Id, next.FireAt));
        }
    }

    [Fact]
    public async Task ScheduleFire_previously_claimed_slot_is_not_rewritten_or_recreated_even_with_a_stale_cursor()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        await Execute("INSERT INTO schedule_fires(schedule_id,fire_at,outcome,reason) VALUES($1,$2,2,'retained history')",
            schedule.Id, schedule.Timer.FireAt);
        Assert.Equal(schedule.Timer.FireAt.AddMinutes(1), (await engine.Scheduler.FireAsync(schedule.Timer, Token))!.FireAt);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal((short)2, await Scalar("SELECT outcome FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal("retained history", await Scalar("SELECT reason FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
    }

    [Fact]
    public async Task ScheduleFire_database_fence_rejects_a_locally_live_old_epoch_after_takeover()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var first = new Engine(database.DataSource, server);
        await using var second = new Engine(database.DataSource, server);
        Assert.True(await first.Lease.TryAcquireOrRenewAsync(Token));
        await Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second' WHERE name='engine'");
        Assert.True(await second.Lease.TryAcquireOrRenewAsync(Token));
        Assert.True(first.Lease.IsLeader);
        await Assert.ThrowsAsync<NotLeaderException>(() => first.Scheduler.FireAsync(schedule.Timer, Token));
        Assert.False(first.Lease.IsLeader);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        await second.Scheduler.FireAsync(schedule.Timer, Token);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
    }

    [Fact]
    public async Task ScheduleFire_two_hosted_engines_reload_committed_notifications_and_recover_scheduled_runs_without_commands()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        var later = schedule.Timer.FireAt.AddHours(1);
        await Execute("UPDATE schedules SET next_fire_at=$2 WHERE id=$1", schedule.Id, later);
        await using var leader = new Engine(database.DataSource, server);
        await using var follower = new Engine(database.DataSource, server);
        Assert.True(await leader.Lease.TryAcquireOrRenewAsync(Token));
        await leader.Start();
        await follower.Start();
        await WaitFor(() => leader.Scheduler.IsReady);
        Assert.False(follower.Scheduler.IsReady);
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        {
            await using (var transaction = await connection.BeginTransactionAsync(Token))
            {
                await EditAndNotify(connection, transaction);
                await transaction.RollbackAsync(Token);
            }
            Assert.Equal(later, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
            var elapsed = Stopwatch.StartNew();
            await using (var transaction = await connection.BeginTransactionAsync(Token))
            {
                await EditAndNotify(connection, transaction);
                await transaction.CommitAsync(Token);
            }
            await WaitFor(async () => (long)(await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series))! == 1);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"NOTIFY reload took {elapsed.Elapsed}; heartbeat is 5 s.");
        }
        var run = Assert.IsType<Guid>(await Scalar("SELECT run_id FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        await WaitFor(() => leader.Recovery.RecoveredRuns.ContainsKey(run));
        Assert.Equal("notified", leader.Recovery.RecoveredRuns[run].State.Snapshot.Variables["env"].GetString());
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1", run));
        await leader.Stop();
        await WaitFor(() => follower.Scheduler.IsReady);
        Assert.True(follower.Recovery.RecoveredRuns.ContainsKey(run));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));

        async Task EditAndNotify(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            await using var edit = new NpgsqlCommand("""
                WITH edited AS (
                    UPDATE schedules SET next_fire_at=$2,variables='{"env":"notified"}',row_version=row_version+1
                    WHERE id=$1 RETURNING workflow_id
                )
                SELECT pg_notify('schedules',workflow_id) FROM edited
                """, connection, transaction);
            edit.Parameters.AddWithValue(schedule.Id);
            edit.Parameters.AddWithValue(schedule.Timer.FireAt);
            await edit.ExecuteNonQueryAsync(Token);
        }
    }

    [Fact]
    public async Task ScheduleFire_a_rejected_schedule_does_not_starve_other_due_schedules_in_the_hosted_heap()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var rejected = await CreateSchedule(server);
        var valid = await CreateSchedule(server);
        await Execute("UPDATE schedules SET version_pin='9.*' WHERE id=$1", rejected.Id);
        await using var engine = new Engine(database.DataSource, server);
        await engine.Start();
        await WaitFor(async () => (long)(await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", valid.Series))! == 1);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", rejected.Id));
        Assert.Equal(rejected.Timer.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", rejected.Id));
        Assert.False(engine.Scheduler.ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task ScheduleFire_listener_reconnect_reloads_a_commit_missed_while_disconnected()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await Execute("UPDATE schedules SET next_fire_at=$2 WHERE id=$1", schedule.Id, schedule.Timer.FireAt.AddHours(1));
        var name = "wf-scheduler-" + Guid.NewGuid().ToString("N");
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = name
        }.ConnectionString);
        await using var engine = new Engine(source, server);
        await engine.Start();
        await WaitFor(() => engine.Scheduler.IsReady);
        var pid = Assert.IsType<int>(await Scalar("""
            SELECT pid FROM pg_stat_activity
            WHERE application_name=$1 AND query IN ('LISTEN registry; LISTEN schedules','SELECT 1')
            """, name));
        Assert.Equal(true, await Scalar("SELECT pg_terminate_backend($1)", pid));
        await WaitFor(() => !engine.Scheduler.IsReady || engine.Scheduler.ExecuteTask!.IsCompleted);
        Assert.False(engine.Scheduler.ExecuteTask!.IsCompleted, engine.Scheduler.ExecuteTask.Exception?.ToString());
        await Execute("UPDATE schedules SET next_fire_at=$2 WHERE id=$1", schedule.Id, schedule.Timer.FireAt);
        await WaitFor(async () => (long)(await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series))! == 1);
        await WaitFor(() => engine.Scheduler.IsReady);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
    }

    private async Task<Schedule> CreateSchedule(Server server, string overlap = "Skip")
    {
        var request = await Request(server);
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [new("env", VariableType.String, true, JsonSerializer.SerializeToElement("fallback"))]
        };
        request = request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest),
            Schedules = $$$"""{"instances":[{"id":"timer","interval":"00:01:00","timezone":"Etc/UTC","overlap":"{{{overlap}}}","variables":{"env":"first"}}]}""",
            ScheduleSourcePath = "workflow.schedules.json"
        };
        using var registered = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => server.Versions.IsReady && HasVersion());
        var id = Assert.IsType<Guid>(await Scalar("SELECT id FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        var series = Assert.IsType<Guid>(await Scalar("SELECT id FROM run_series WHERE schedule_id=$1", id));
        var fireAt = new DateTimeOffset(server.Clock.Now.UtcTicks / 10 * 10, TimeSpan.Zero);
        server.Clock.Now = fireAt;
        await Execute("UPDATE schedules SET next_fire_at=$2 WHERE id=$1", id, fireAt);
        return new(request, id, series, version, new(id, fireAt));

        bool HasVersion()
        {
            try { return server.Versions.GetActive(request.WorkflowId!).VersionId == version; }
            catch (KeyNotFoundException) { return false; }
        }
    }

    private static JsonElement WithVersion(RegistrationRequest request, string version) =>
        JsonSerializer.SerializeToElement(
            request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with { Version = version },
            WorkflowManifestJsonContext.Default.WorkflowManifest);

    private async Task<object?> Scalar(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        var result = await query.ExecuteScalarAsync(Token);
        return result is DateTime timestamp ? new DateTimeOffset(timestamp) : result;
    }

    private async Task Execute(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        await query.ExecuteNonQueryAsync(Token);
    }

    private static Task WaitFor(Func<bool> condition) => WaitFor(() => Task.FromResult(condition()));

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed record Schedule(RegistrationRequest Request, Guid Id, Guid Series, Guid Version, ScheduleTimer Timer)
    {
        public string Workflow => Request.WorkflowId!;
    }

    private sealed class Engine : IAsyncDisposable
    {
        public LeaseManager Lease { get; }
        public RunStore Store { get; }
        public RunRecovery Recovery { get; }
        public ScheduleFireService Scheduler { get; }
        private bool stopped;

        public Engine(NpgsqlDataSource source, Server server)
        {
            Lease = new(source, new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5300")),
                NullLogger<LeaseManager>.Instance);
            Store = new(source, NullLogger<RunStore>.Instance, Lease);
            Recovery = new(source, Store, Lease, server.Clock, NullLogger<RunRecovery>.Instance);
            Scheduler = new(source, Lease, Recovery, server.Versions, new(server.Clock), server.Clock,
                NullLogger<ScheduleFireService>.Instance);
        }

        public async Task Start()
        {
            await Lease.StartAsync(Token);
            await Recovery.StartAsync(Token);
            await Scheduler.StartAsync(Token);
        }

        public async Task Stop()
        {
            if (stopped) return;
            await Scheduler.StopAsync(Token);
            await Recovery.StopAsync(Token);
            await Lease.StopAsync(Token);
            stopped = true;
        }

        public async ValueTask DisposeAsync()
        {
            await Stop();
            Scheduler.Dispose();
            Recovery.Dispose();
            Lease.Dispose();
        }
    }
}
