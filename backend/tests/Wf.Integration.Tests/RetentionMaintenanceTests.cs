using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Artifacts;
using Wf.ControlPlane.Artifacts;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Maintenance;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Registration;
using Wf.Integration.Tests.Reports;

namespace Wf.Integration.Tests;

public sealed class RetentionMaintenanceTests(RegistrationHostDatabase database) : IClassFixture<RegistrationHostDatabase>, IAsyncLifetime
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly Clock clock = new();
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf-retention-" + Guid.NewGuid().ToString("N"));
    private FileSystemBlobStore blobs = null!;

    public async ValueTask InitializeAsync()
    {
        blobs = new(root, RandomNumberGenerator.GetBytes(32));
        await Execute("""
            TRUNCATE runs,report_ids,daily_rollups,dashboard_rollups,retention_blob_deletions,commands,audit_log CASCADE;
            UPDATE retention_config SET default_days=90,audit_days=365;
            UPDATE workflow_types SET retention_days=NULL;
            """);
    }

    public ValueTask DisposeAsync()
    {
        blobs.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private RetentionPurger Purger(IBlobStore? store = null, LeaseManager? leadership = null) =>
        new(database.DataSource, store ?? blobs, clock, NullLogger<RetentionPurger>.Instance, leadership);
    private PartitionMaintenance Partitions() => new(database.DataSource, clock);

    [Fact]
    public async Task Per_type_shorter_and_longer_overrides_keep_unexpired_runs_and_independent_audit()
    {
        var normal = await Workflow();
        var shorter = await Workflow(14);
        var longer = await Workflow(180);
        var expired = await Run(shorter, clock.GetUtcNow().AddDays(-45));
        var retained = new[]
        {
            await Run(normal, clock.GetUtcNow().AddDays(-45)),
            await Run(longer, clock.GetUtcNow().AddDays(-120)),
            await Run(shorter, clock.GetUtcNow().AddDays(-5))
        };
        await Audit(clock.GetUtcNow().AddDays(-364));
        await Audit(clock.GetUtcNow().AddDays(-366));
        Assert.Equal(1, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(0, await Count("SELECT count(*) FROM runs WHERE id=$1", expired));
        foreach (var run in retained) Assert.Equal(1, await Count("SELECT count(*) FROM runs WHERE id=$1", run));
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_log"));
        Assert.Equal(1, await Count("SELECT count(*) FROM retention_blob_deletions WHERE kind=0 AND object_id=$1", expired.ToString("D")));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("attempt")]
    [InlineData("node")]
    [InlineData("reservation")]
    [InlineData("queue")]
    [InlineData("dispatch")]
    [InlineData("command")]
    [InlineData("log-recovery")]
    [InlineData("grant")]
    public async Task Active_physical_work_or_pending_recovery_is_never_purged(string guard)
    {
        var workflow = await Workflow();
        var run = await Run(workflow, clock.GetUtcNow().AddDays(-120));
        await Attempt(run);
        switch (guard)
        {
            case "running":
                await Execute("UPDATE runs SET status=1,ended_at=NULL WHERE id=$1", run);
                break;
            case "attempt":
                await Execute("UPDATE task_attempts SET status=3 WHERE run_id=$1", run);
                break;
            case "node":
                await Execute("UPDATE node_runs SET status=3 WHERE run_id=$1", run);
                break;
            case "reservation":
                await Agent();
                await Execute("""
                    INSERT INTO reservations(id,run_day,run_id,node_key,agent_id,selector,created_at,renewed_at,expires_at)
                    VALUES($1,uuid_v7_run_day($2),$2,'work',$3,'{}',$4,$4,$4+interval '1 day')
                    """, Guid.CreateVersion7(), run, AgentId, clock.GetUtcNow().AddDays(-120));
                break;
            case "queue":
                await Execute("""
                    INSERT INTO reservation_queue(run_day,run_id,node_key,machine_count,selector,lifetime,queued_at)
                    VALUES(uuid_v7_run_day($1),$1,'work',1,'{}',interval '1 hour',$2)
                    """, run, clock.GetUtcNow().AddDays(-120));
                break;
            case "dispatch":
                await Agent();
                await Execute("""
                    INSERT INTO dispatch_outbox(run_day,run_id,task_key,attempt,agent_id,payload)
                    VALUES(uuid_v7_run_day($1),$1,'work/task',1,$2,''::bytea)
                    """, run, AgentId);
                break;
            case "command":
                await Execute("""
                    INSERT INTO commands(id,kind,run_id,payload,created_by,created_at)
                    VALUES($1,1,$2,'{}','test',$3)
                    """, Guid.CreateVersion7(), run, clock.GetUtcNow().AddDays(-120));
                break;
            case "log-recovery":
                await Execute("UPDATE runs SET deadline_at=$2 WHERE id=$1", run, clock.GetUtcNow().AddHours(-12));
                break;
            case "grant":
                await Artifact(run, new string('a', 64), committed: false, expiry: clock.GetUtcNow().AddHours(1));
                break;
        }
        Assert.Equal(0, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(1, await Count("SELECT count(*) FROM runs WHERE id=$1", run));
        Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions"));
    }

    [Fact]
    public async Task Five_thousand_run_batches_commit_exactly_and_refresh_retained_daily_metrics()
    {
        var workflow = await Workflow();
        var at = clock.GetUtcNow().AddDays(-120);
        var ids = Enumerable.Range(0, 5001).Select(_ => Guid.CreateVersion7(at)).ToArray();
        await Execute("SELECT ensure_run_partitions($1)", DateOnly.FromDateTime(at.UtcDateTime));
        await Execute("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,ended_at,deadline_at)
            SELECT uuid_v7_run_day(id),id,$2,$3,0,'test','{}','{}',3,$4,$4+interval '1 minute',$4+interval '1 day'
            FROM unnest($1::uuid[]) id
            """, ids, workflow, Guid.CreateVersion7(), at);
        await Execute("SELECT refresh_daily_rollups($1,$2)", DateOnly.FromDateTime(at.UtcDateTime), workflow);
        Assert.Equal(5000, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(1, await Count("SELECT count(*) FROM runs WHERE workflow_id=$1", workflow));
        Assert.Equal(5000, await Count("SELECT count(*) FROM retention_blob_deletions WHERE kind=0"));
        Assert.Equal(1, await Count("SELECT runs FROM daily_rollups WHERE workflow_id=$1 AND node_key=''", workflow));
        Assert.Equal(1, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(0, await Count("SELECT count(*) FROM daily_rollups WHERE workflow_id=$1", workflow));
        Assert.Equal(0, await Purger().PurgeBatchAsync(Token));
    }

    [Fact]
    public async Task Paced_blob_batches_stop_at_thirty_two_and_leave_the_remainder_durable()
    {
        var workflow = await Workflow();
        for (var index = 0; index < 33; index++)
            await Run(workflow, clock.GetUtcNow().AddDays(-120));
        Assert.Equal(33, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(32, await Purger().DeleteBlobBatchAsync(Token));
        Assert.Equal(1, await Count("SELECT count(*) FROM retention_blob_deletions"));
        Assert.Equal(1, await Purger().DeleteBlobBatchAsync(Token));
        Assert.Equal(0, await Purger().DeleteBlobBatchAsync(Token));
        Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions"));
    }

    [Fact]
    public async Task Database_failure_rolls_back_child_rows_blob_queue_and_metrics_before_any_storage_delete()
    {
        var workflow = await Workflow();
        var run = await Run(workflow, clock.GetUtcNow().AddDays(-120));
        await Attempt(run);
        await Execute("INSERT INTO run_events(run_day,run_id,seq,ts,kind,payload) VALUES(uuid_v7_run_day($1),$1,1,$2,0,''::bytea)", run, clock.GetUtcNow());
        await Execute("SELECT refresh_daily_rollups(uuid_v7_run_day($1),$2)", run, workflow);
        var name = "reject_retention_delete_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected retention delete'; END $$;
            CREATE TRIGGER {name} BEFORE DELETE ON runs FOR EACH ROW EXECUTE FUNCTION {name}();
            """);
        try { await Assert.ThrowsAsync<PostgresException>(() => Purger().PurgeBatchAsync(Token)); }
        finally { await Execute($"DROP TRIGGER {name} ON runs; DROP FUNCTION {name}();"); }
        Assert.Equal(1, await Count("SELECT count(*) FROM runs WHERE id=$1", run));
        Assert.Equal(1, await Count("SELECT count(*) FROM task_attempts WHERE run_id=$1", run));
        Assert.Equal(1, await Count("SELECT count(*) FROM run_events WHERE run_id=$1", run));
        Assert.Equal(1, await Count("SELECT runs FROM daily_rollups WHERE workflow_id=$1 AND node_key=''", workflow));
        Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions"));
        Assert.Equal(1, await Purger().PurgeBatchAsync(Token));
    }

    [Fact]
    public async Task Shared_artifact_bytes_survive_until_the_last_retained_run_and_grant_are_gone()
    {
        var workflow = await Workflow();
        var old = await Run(workflow, clock.GetUtcNow().AddDays(-120));
        var recent = await Run(workflow, clock.GetUtcNow().AddDays(-5));
        await Attempt(old);
        await Attempt(recent);
        var bytes = Encoding.UTF8.GetBytes("shared immutable artifact");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var content = new BlobContent(BlobKind.Artifacts, hash, bytes.Length);
        using (var input = new MemoryStream(bytes)) await blobs.UploadAsync(content, input, Token);
        await Artifact(old, hash, size: bytes.Length);
        await Artifact(recent, hash, size: bytes.Length);
        Assert.Equal(1, await Purger().PurgeBatchAsync(Token));
        while (await Purger().DeletePendingBlobsAsync(Token)) { }
        await blobs.DownloadAsync(content, Stream.Null, Token);
        clock.Now = clock.Now.AddDays(120);
        Assert.Equal(1, await Purger().PurgeBatchAsync(Token));
        while (await Purger().DeletePendingBlobsAsync(Token)) { }
        await Assert.ThrowsAsync<FileNotFoundException>(() => blobs.DownloadAsync(content, Stream.Null, Token));
    }

    [Fact]
    public async Task Blob_failure_is_durable_sanitized_backed_off_and_retried_by_a_new_worker()
    {
        var workflow = await Workflow();
        var run = await Run(workflow, clock.GetUtcNow().AddDays(-120));
        var content = await Log(run);
        Assert.Equal(1, await Purger().PurgeBatchAsync(Token));
        Assert.True(await Purger(new FailedStore()).DeletePendingBlobsAsync(Token));
        await using (var query = database.DataSource.CreateCommand("SELECT attempts,last_error,next_attempt_at>clock_timestamp() FROM retention_blob_deletions"))
        {
            await using var reader = await query.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("blob_delete_failed", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
        }
        await blobs.DownloadAsync(content, Stream.Null, Token);
        Assert.False(await Purger().DeletePendingBlobsAsync(Token));
        await Execute("UPDATE retention_blob_deletions SET next_attempt_at=clock_timestamp()");
        Assert.True(await Purger().DeletePendingBlobsAsync(Token));
        await Assert.ThrowsAnyAsync<IOException>(() => blobs.DownloadAsync(content, Stream.Null, Token));
        Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions"));
    }

    [Fact]
    public async Task Orphan_inventory_and_hash_gate_reclaim_failed_publications_without_racing_an_upload()
    {
        var bytes = Encoding.UTF8.GetBytes("failed artifact publication");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var content = new BlobContent(BlobKind.Artifacts, hash, bytes.Length);
        using (var input = new MemoryStream(bytes)) await blobs.UploadAsync(content, input, Token);
        await Purger().QueueOrphanArtifactsAsync(Token);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await using var held = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1464226382,hashtext($1))", connection);
            held.Parameters.AddWithValue(hash);
            await held.ExecuteNonQueryAsync(Token);
            Assert.True(await Purger().DeletePendingBlobsAsync(Token));
            await blobs.DownloadAsync(content, Stream.Null, Token);
            await transaction.CommitAsync(Token);
        }
        await Execute("UPDATE retention_blob_deletions SET next_attempt_at=clock_timestamp()");
        Assert.True(await Purger().DeletePendingBlobsAsync(Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => blobs.DownloadAsync(content, Stream.Null, Token));
    }

    [Fact]
    public async Task Generalized_orphan_sweeper_removes_late_log_uploads_but_preserves_retained_run_prefixes()
    {
        var workflow = await Workflow();
        var retained = await Run(workflow, clock.GetUtcNow().AddDays(-5));
        var missing = Guid.CreateVersion7(clock.GetUtcNow().AddDays(-120));
        var keep = await Log(retained);
        var remove = await Log(missing);
        await new ResourceRetentionService(database.DataSource, blobs, NullLogger<ResourceRetentionService>.Instance).SweepAsync(Token);
        await blobs.DownloadAsync(keep, Stream.Null, Token);
        await Assert.ThrowsAnyAsync<IOException>(() => blobs.DownloadAsync(remove, Stream.Null, Token));
    }

    [Fact]
    public async Task Whole_expired_partition_families_detach_without_cascade_or_foreign_key_loss_and_never_drop_active_months()
    {
        var old = new DateOnly(2024, 1, 1);
        await Execute("SELECT ensure_run_partitions($1)", old);
        var dropped = await Partitions().DropExpiredEmptyAsync(Token);
        Assert.Equal(7, dropped);
        Assert.Equal(0, await Count("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema() AND c.relname LIKE '%_202401'"));
        var workflow = await Workflow();
        var active = await Run(workflow, new DateTimeOffset(2024, 2, 5, 0, 0, 0, TimeSpan.Zero));
        await Execute("UPDATE runs SET status=1,ended_at=NULL WHERE id=$1", active);
        await Partitions().DropExpiredEmptyAsync(Token);
        Assert.Equal(7, await Count("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema() AND c.relname LIKE '%_202402'"));
        await using var invalid = database.DataSource.CreateCommand("""
            INSERT INTO run_events(run_day,run_id,seq,ts,kind,payload) VALUES(uuid_v7_run_day($1),$1,1,$2,0,''::bytea)
            """);
        invalid.Parameters.AddWithValue(Guid.CreateVersion7(new DateTimeOffset(2024, 2, 6, 0, 0, 0, TimeSpan.Zero)));
        invalid.Parameters.AddWithValue(clock.GetUtcNow());
        var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync(Token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task Next_month_run_and_audit_partitions_are_precreated_idempotently_and_outside_off_peak()
    {
        clock.Now = new(2027, 11, 30, 20, 0, 0, TimeSpan.Zero);
        await Partitions().EnsureAsync(Token);
        await Partitions().EnsureAsync(Token);
        foreach (var suffix in new[] { "202711", "202712" })
            Assert.Equal(8, await Count("""
                SELECT count(*) FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid
                JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema()
                    AND c.relname IN('runs_'||$1,'run_events_'||$1,'run_snapshots_'||$1,'node_runs_'||$1,
                        'task_attempts_'||$1,'reports_'||$1,'artifacts_'||$1,'audit_log_'||$1)
                """, suffix));
        Assert.Equal(0, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(0, await Partitions().DropExpiredEmptyAsync(Token));
    }

    [Fact]
    public async Task Stale_engine_lease_cannot_delete_any_expired_data()
    {
        var workflow = await Workflow();
        var run = await Run(workflow, clock.GetUtcNow().AddDays(-120));
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"), new Uri("http://127.0.0.1:8080")),
            NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        await Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second' WHERE name='engine'");
        await Assert.ThrowsAsync<NotLeaderException>(() => Purger(leadership: leader).PurgeBatchAsync(Token));
        Assert.Equal(1, await Count("SELECT count(*) FROM runs WHERE id=$1", run));
        Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions"));
    }

    [Fact]
    public async Task Production_one_second_fence_and_renewal_remain_intact_during_paced_bulk_retention()
    {
        var workflow = await Workflow();
        var at = clock.GetUtcNow().AddDays(-120);
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.CreateVersion7(at)).ToArray();
        await Execute("SELECT ensure_run_partitions($1)", DateOnly.FromDateTime(at.UtcDateTime));
        await Execute("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,ended_at,deadline_at)
            SELECT uuid_v7_run_day(id),id,$2,$3,0,'test','{}','{}',3,$4,$4+interval '1 minute',$4+interval '1 day'
            FROM unnest($1::uuid[]) id
            """, ids, workflow, Guid.CreateVersion7(), at);
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"), new Uri("http://127.0.0.1:8080")),
            NullLogger<LeaseManager>.Instance);
        await leader.StartAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (!leader.IsLeader) await Task.Delay(10, deadline.Token);
            Assert.Equal(1001, await Purger(leadership: leader).PurgeBatchAsync(Token));
            Assert.True(leader.IsLeader);
            Assert.Equal(0, await Count("SELECT count(*) FROM runs WHERE workflow_id=$1", workflow));
        }
        finally { await leader.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Independent_audit_purge_keeps_the_exact_one_year_boundary_and_uses_five_thousand_row_batches()
    {
        var boundary = clock.GetUtcNow().AddDays(-365);
        await Audit(boundary);
        await Audit(boundary.AddTicks(-10));
        await Execute("""
            INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,summary)
            SELECT $1,'test','User','test','Run',id::text,'{}' FROM generate_series(1,5000) id
            """, boundary.AddMinutes(-1));
        Assert.Equal(0, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(2, await Count("SELECT count(*) FROM audit_log"));
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE ts=$1", boundary));
        Assert.Equal(0, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_log"));
        await Execute("UPDATE retention_config SET audit_days=730");
        await Audit(clock.GetUtcNow().AddDays(-500));
        Assert.Equal(0, await Purger().PurgeBatchAsync(Token));
        Assert.Equal(2, await Count("SELECT count(*) FROM audit_log"));
    }

    [Fact]
    public async Task Concurrent_blob_workers_claim_one_tombstone_and_cancellation_never_acknowledges_deletion()
    {
        var run = Guid.CreateVersion7(clock.GetUtcNow().AddDays(-120));
        await Execute("INSERT INTO retention_blob_deletions(kind,object_id) VALUES(0,$1)", run.ToString("D"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Purger(new CanceledStore(cancel)).DeletePendingBlobsAsync(cancel.Token));
        Assert.Equal(1, await Count("SELECT count(*) FROM retention_blob_deletions WHERE attempts=0"));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Purger().DeletePendingBlobsAsync(Token)));
        Assert.Single(results, claimed => claimed);
        Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions"));
    }

    [Fact]
    public async Task Full_run_graph_report_locators_released_reservations_and_metrics_purge_atomically_before_blob_cleanup()
    {
        var workflow = await Workflow();
        var at = clock.GetUtcNow().AddDays(-120);
        var expired = await Run(workflow, at);
        var retained = await Run(workflow, at);
        await Execute("UPDATE runs SET ended_at=$2 WHERE id=$1", retained, clock.GetUtcNow());
        await Attempt(expired);
        await Attempt(retained);
        await Agent();
        await Execute("""
            INSERT INTO reservations(id,run_day,run_id,node_key,agent_id,selector,created_at,renewed_at,expires_at,released_at,release_reason)
            VALUES($1,uuid_v7_run_day($2),$2,'work',$3,'{}',$4,$4,$4+interval '1 day',$4+interval '1 hour','physical Ack')
            """, Guid.CreateVersion7(), expired, AgentId, at);
        await Execute("""
            INSERT INTO commands(id,kind,run_id,payload,created_by,created_at,processed_at)
            VALUES($1,1,$2,'{}','test',$3,$3+interval '1 minute')
            """, Guid.CreateVersion7(), expired, at);
        await Execute("INSERT INTO run_events(run_day,run_id,seq,ts,kind,payload) VALUES(uuid_v7_run_day($1),$1,1,$2,0,''::bytea)", expired, at);
        await Execute("INSERT INTO run_snapshots(run_day,run_id,seq,created_at,payload) VALUES(uuid_v7_run_day($1),$1,0,$2,''::bytea)", expired, at);
        var bytes = Encoding.UTF8.GetBytes("expired report and artifact");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var reportId = Guid.CreateVersion7();
        var report = new BlobContent(BlobKind.Reports, hash, bytes.Length, $"reports/{expired:D}/{reportId:D}");
        using (var input = new MemoryStream(bytes)) await blobs.UploadAsync(report, input, Token);
        await Execute("INSERT INTO report_ids(id,run_day) VALUES($1,uuid_v7_run_day($2))", reportId, expired);
        await Execute("""
            INSERT INTO reports(run_day,id,run_id,source_key,attempt,kind,title,tags,size_bytes,blob_uri,sha256,created_at)
            VALUES(uuid_v7_run_day($1),$2,$1,'work/task',1,'text','Report','{}',$3,$4,decode($5,'hex'),$6)
            """, expired, reportId, (long)bytes.Length, report.Key, hash, at);
        var artifact = new BlobContent(BlobKind.Artifacts, hash, bytes.Length);
        using (var input = new MemoryStream(bytes)) await blobs.UploadAsync(artifact, input, Token);
        await Artifact(expired, hash, size: bytes.Length);
        var expiredLog = await Log(expired);
        var retainedLog = await Log(retained);
        var subscription = Guid.CreateVersion7();
        await Execute("""
            INSERT INTO subscriptions(id,principal_id,scope_kind,scope_id,type_scope,events,channel,target)
            VALUES($1,$2,'workflow',$3,$4,ARRAY['Completed'],'Email','subscriber@example.test')
            """, subscription, Guid.NewGuid(), workflow, workflow[..workflow.LastIndexOf('/')]);
        await Execute("""
            INSERT INTO notification_outbox(id,subscription_id,run_day,run_id,payload)
            VALUES($1,$2,uuid_v7_run_day($3),$3,'{}')
            """, Guid.CreateVersion7(), subscription, expired);
        await Execute("SELECT refresh_daily_rollups(uuid_v7_run_day($1),$2)", expired, workflow);
        Assert.Equal(1, await Purger().PurgeBatchAsync(Token));
        foreach (var table in new[] { "task_attempts", "node_runs", "run_events", "run_snapshots", "reports",
            "artifacts", "artifact_grants", "reservations", "commands", "notification_outbox" })
            Assert.Equal(0, await Count($"SELECT count(*) FROM {table} WHERE run_id=$1", expired));
        Assert.Equal(0, await Count("SELECT count(*) FROM report_ids WHERE id=$1", reportId));
        Assert.Equal(1, await Count("SELECT count(*) FROM subscriptions WHERE id=$1", subscription));
        Assert.Equal(1, await Count("SELECT runs FROM daily_rollups WHERE workflow_id=$1 AND node_key=''", workflow));
        Assert.Equal(1, await Count("SELECT observations FROM dashboard_rollups WHERE workflow_id=$1 AND scope='workflow'", workflow));
        await blobs.DownloadAsync(report, Stream.Null, Token);
        while (await Purger().DeletePendingBlobsAsync(Token)) { }
        foreach (var content in new[] { report, expiredLog, artifact })
            await Assert.ThrowsAnyAsync<IOException>(() => blobs.DownloadAsync(content, Stream.Null, Token));
        await blobs.DownloadAsync(retainedLog, Stream.Null, Token);
    }

    [Fact]
    public async Task Global_maximum_includes_long_type_overrides_before_any_partition_drop()
    {
        await Workflow(3650);
        await Execute("SELECT ensure_run_partitions($1)", new DateOnly(2024, 1, 1));
        await Partitions().DropExpiredEmptyAsync(Token);
        Assert.Equal(7, await Count("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND c.relname LIKE '%_202401'
            """));
    }

    [Fact]
    public async Task Real_artifact_publication_holds_the_same_hash_gate_as_GC_until_its_reference_commits()
    {
        await using var fixture = await ReportReadFixture.Start(database.DataSource, blobs);
        var attempt = await fixture.Seed();
        ArtifactActor actor;
        await using (var query = database.DataSource.CreateCommand("""
            SELECT a.dispatch_payload,a.agent_id,g.session_id FROM task_attempts a JOIN agents g ON g.id=a.agent_id
            WHERE a.run_id=$1 AND a.task_key=$2 AND a.attempt=$3
            """))
        {
            query.Parameters.AddWithValue(attempt.RunId);
            query.Parameters.AddWithValue(attempt.TaskKey);
            query.Parameters.AddWithValue(attempt.Attempt);
            await using var reader = await query.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            var dispatch = Dispatch.Parser.ParseFrom(reader.GetFieldValue<byte[]>(0));
            actor = new(attempt.RunId, attempt.TaskKey, attempt.Attempt, reader.GetGuid(1), reader.GetGuid(2),
                checked((long)dispatch.ReservationEpoch), dispatch.DeadlineAt.ToDateTimeOffset().UtcTicks);
        }
        var bytes = Encoding.UTF8.GetBytes("live artifact publication guarded from GC");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var blocked = new BlockedStore(blobs);
        var service = new ArtifactGrantService(database.DataSource, blocked, TimeProvider.System);
        var id = Guid.CreateVersion7();
        await service.GrantAsync(actor, id, new("response.txt", "text/plain", bytes.Length, hash), Token);
        await Execute("INSERT INTO retention_blob_deletions(kind,object_id) VALUES(1,$1)", hash);
        using var input = new MemoryStream(bytes);
        var publication = service.PublishAsync(actor, id, input, bytes.Length, Token);
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(await Purger().DeletePendingBlobsAsync(Token));
            Assert.Equal(1, await Count("SELECT count(*) FROM retention_blob_deletions WHERE object_id=$1", hash));
        }
        finally { blocked.Resume.TrySetResult(); }
        var receipt = await publication;
        Assert.Equal(id, receipt.ArtifactId);
        await Execute("UPDATE retention_blob_deletions SET next_attempt_at=clock_timestamp()");
        Assert.True(await Purger().DeletePendingBlobsAsync(Token));
        Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions WHERE object_id=$1", hash));
        await blobs.DownloadAsync(new(BlobKind.Artifacts, hash, bytes.Length), Stream.Null, Token);
    }

    [Fact]
    public async Task Actual_hosted_maintenance_renews_leadership_precreates_next_month_and_cleans_rows_and_blobs()
    {
        clock.Now = new(2035, 12, 17, 2, 30, 0, TimeSpan.Zero);
        var workflow = await Workflow();
        var expired = await Run(workflow, clock.GetUtcNow().AddDays(-120));
        var retained = await Run(workflow, clock.GetUtcNow().AddDays(-5));
        var remove = await Log(expired);
        var keep = await Log(retained);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton<IBlobStore>(blobs);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(new LeaseOptions(Guid.NewGuid().ToString("N"), new Uri("http://127.0.0.1:8080")));
        builder.Services.AddSingleton<LeaseManager>();
        builder.Services.AddHostedService<LeaseManager>(services => services.GetRequiredService<LeaseManager>());
        builder.Services.AddSingleton<RetentionPurger>();
        builder.Services.AddSingleton<PartitionMaintenance>();
        builder.Services.AddHostedService<RetentionService>();
        using var host = builder.Build();
        Assert.True(await host.Services.GetRequiredService<LeaseManager>().TryAcquireOrRenewAsync(Token));
        await host.StartAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            while (await Count("SELECT count(*) FROM runs WHERE id=$1", expired) != 0 ||
                await Count("SELECT count(*) FROM retention_blob_deletions") != 0)
                await Task.Delay(25, deadline.Token);
            Assert.Equal(1, await Count("SELECT count(*) FROM runs WHERE id=$1", retained));
            Assert.Equal(8, await Count("""
                SELECT count(*) FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid
                JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema() AND c.relname LIKE '%_203601'
                """));
            await Assert.ThrowsAnyAsync<IOException>(() => blobs.DownloadAsync(remove, Stream.Null, Token));
            await blobs.DownloadAsync(keep, Stream.Null, Token);
            Assert.True(host.Services.GetRequiredService<LeaseManager>().IsLeader);
        }
        finally { await host.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Busy_expired_partition_defers_without_waiting_for_a_reader_or_dropping_part_of_its_family()
    {
        await Execute("SELECT ensure_run_partitions($1)", new DateOnly(2020, 1, 1));
        await using var reader = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await reader.BeginTransactionAsync(Token);
        await using var held = new NpgsqlCommand("LOCK TABLE runs_202001 IN ACCESS SHARE MODE", reader);
        await held.ExecuteNonQueryAsync(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<PostgresException>(() => Partitions().DropExpiredEmptyAsync(deadline.Token));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        Assert.Equal(7, await Count("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND c.relname LIKE '%_202001'
            """));
        await transaction.CommitAsync(Token);
        Assert.Equal(7, await Partitions().DropExpiredEmptyAsync(Token));
    }

    private async Task<string> Workflow(int? days = null)
    {
        var team = "purger-" + Guid.NewGuid().ToString("N");
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction, new(team, [new(team, "type", "Purge integration", new([]))]), Token);
        await transaction.CommitAsync(Token);
        await Execute("INSERT INTO workflows(id,type_id) VALUES($1,$2)", team + "/type/workflow", team + "/type");
        if (days is not null) await Execute("UPDATE workflow_types SET retention_days=$2 WHERE id=$1", team + "/type", days.Value);
        return team + "/type/workflow";
    }

    private async Task<Guid> Run(string workflow, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7(at);
        await Execute("SELECT ensure_run_partitions(uuid_v7_run_day($1))", id);
        await Execute("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,ended_at,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,0,'test','{}','{}',3,$4,$4+interval '1 minute',$4+interval '1 day')
            """, id, workflow, Guid.CreateVersion7(), at);
        return id;
    }
    private Task Attempt(Guid run) => Execute("""
        WITH node AS (
            INSERT INTO node_runs(run_day,run_id,node_key,status) VALUES(uuid_v7_run_day($1),$1,'work',5)
        )
        INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at)
        SELECT run_day,id,'work/task',1,5,deadline_at FROM runs WHERE id=$1
        """, run);
    private async Task Audit(DateTimeOffset at)
    {
        await Execute("SELECT ensure_audit_partition($1)", DateOnly.FromDateTime(at.UtcDateTime));
        await Execute("""
            INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,'test','User','test','Run','test','{}')
            """, at);
    }
    private async Task Artifact(Guid run, string hash, int size = 1, bool committed = true, DateTimeOffset? expiry = null)
    {
        var id = Guid.CreateVersion7();
        await Execute("""
            INSERT INTO artifact_grants(id,run_day,run_id,source_key,attempt,agent_id,session_id,reservation_epoch,
                name,content_type,size_bytes,sha256,expires_at,committed)
            VALUES($1,uuid_v7_run_day($2),$2,'work/task',1,$3,$4,1,'out.txt','text/plain',$5,decode($6,'hex'),$7,$8)
            """, id, run, Guid.NewGuid(), Guid.NewGuid(), (long)size, hash, expiry ?? clock.GetUtcNow().AddDays(-1), committed);
        if (committed)
            await Execute("""
                INSERT INTO artifacts(run_day,id,run_id,source_key,name,content_type,size_bytes,blob_uri,sha256,created_at)
                VALUES(uuid_v7_run_day($1),$2,$1,'work/task','out.txt','text/plain',$3,'artifacts/'||$4,decode($4,'hex'),$5)
                """, run, id, (long)size, hash, clock.GetUtcNow());
    }
    private static readonly Guid AgentId = Guid.Parse("6c336fb1-f974-47fc-b1a0-4a17809aa14e");
    private Task Agent() => Execute("""
        INSERT INTO agents(id,name,os,arch,cores,ram_mb,max_tasks,labels,pool,status,last_heartbeat_at,agent_version,session_id)
        VALUES($1,'retention',1,1,1,1024,1,'{}','test',0,clock_timestamp(),'test',$2) ON CONFLICT DO NOTHING
        """, AgentId, Guid.NewGuid());
    private async Task<BlobContent> Log(Guid run)
    {
        var bytes = Encoding.UTF8.GetBytes("retention log fixture");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var content = new BlobContent(BlobKind.Logs, hash, bytes.Length, $"logs/{run:D}/{new string('a', 64)}/1/000001.log.gz");
        using var input = new MemoryStream(bytes);
        await blobs.UploadAsync(content, input, Token);
        return content;
    }
    private async Task Execute(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(Token);
    }
    private async Task<long> Count(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 2, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailedStore : IBlobStore
    {
        public Task DeleteRunObjectsAsync(Guid runId, CancellationToken cancellationToken = default) =>
            throw new IOException("private filesystem failure");
        public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class CanceledStore(CancellationTokenSource cancel) : IBlobStore
    {
        public Task DeleteRunObjectsAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            cancel.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not propagated.");
        }
        public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class BlockedStore(IBlobStore inner) : IBlobStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            await inner.UploadAsync(content, source, cancellationToken);
        }
        public Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default) =>
            inner.DownloadAsync(content, destination, cancellationToken);
    }
}
