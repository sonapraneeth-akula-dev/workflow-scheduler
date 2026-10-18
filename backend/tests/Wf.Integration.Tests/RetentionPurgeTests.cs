using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Maintenance;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Integration.Tests.Execution;
using Wf.Integration.Tests.Registration;

namespace Wf.Integration.Tests;

public sealed class RetentionPurgeTests(RegistrationHostDatabase database, ITestOutputHelper output)
    : IClassFixture<RegistrationHostDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private const int ExpiredCount = 5000;
    private const int Lanes = 4;
    private readonly Clock clock = new();
    private sealed record Fixture(Guid[] Runs, BlobContent[] Blobs);
    private sealed record Request(string Path, string Expected);
    private sealed record Sample(double Milliseconds, bool DuringRows);

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Per_type_purge_removes_expired_DB_and_blobs_preserves_live_data_and_API_p95_increases_less_than_ten_percent()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-retention-load-" + Guid.NewGuid().ToString("N"));
        using var blobs = new FileSystemBlobStore(root, RandomNumberGenerator.GetBytes(32));
        try
        {
            var (team, group) = await Registry();
            var expired = await Seed(team + "/short/workflow", clock.GetUtcNow().AddDays(-45), ExpiredCount, blobs);
            var retained = new[]
            {
                await Seed(team + "/normal/workflow", clock.GetUtcNow().AddDays(-45), 50, blobs),
                await Seed(team + "/long/workflow", clock.GetUtcNow().AddDays(-120), 50, blobs),
                await Seed(team + "/short/workflow", clock.GetUtcNow().AddDays(-5), 50, blobs)
            };
            var live = await LiveRuns(team);
            await using var api = await MetricsApiServer.Start(database.DataSource, Token);
            var jwt = api.Jwt(group, lifetime: TimeSpan.FromMinutes(30));
            var requests = new List<Request>();
            foreach (var run in live)
            {
                foreach (var endpoint in new[] { "metrics", "timeline" })
                {
                    var path = $"/api/v1/runs/{run:D}/{endpoint}";
                    using var response = await api.Get(path, jwt, Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var json = await response.Content.ReadAsStringAsync(Token);
                    if (endpoint == "metrics")
                    {
                        var metrics = JsonSerializer.Deserialize(json, MetricsJsonContext.Default.RunMetricsResponse)!;
                        Assert.Equal("Running", metrics.Status);
                        Assert.Equal("Running", metrics.Tasks["node/task"].Status);
                        Assert.Single(metrics.Tasks["node/task"].Attempts);
                    }
                    else
                    {
                        var timeline = JsonSerializer.Deserialize(json, RunTimelineJsonContext.Default.RunTimelineResponse)!;
                        Assert.Single(timeline.Attempts);
                    }
                    requests.Add(new(path, json));
                }
            }
            using var leader = new LeaseManager(database.DataSource,
                new(Guid.NewGuid().ToString("N"), new Uri("http://127.0.0.1:8080")), NullLogger<LeaseManager>.Instance);
            await leader.StartAsync(Token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (!leader.IsLeader) await Task.Delay(10, deadline.Token);
                var purger = new RetentionPurger(database.DataSource, blobs, clock, NullLogger<RetentionPurger>.Instance, leader);
                var partitions = new PartitionMaintenance(database.DataSource, clock, leader);
                await partitions.EnsureAsync(Token);
                await Analyze();
                await Measure(api, jwt, requests, 100, null, null);
                var baseline = await Measure(api, jwt, requests, 1500, null, null);
                await Analyze();
                var rows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var maintenance = Maintain();
                var traffic = Measure(api, jwt, requests, null, maintenance, rows.Task);
                start.SetResult();
                Sample[] during;
                try { during = await traffic; }
                finally { await maintenance; }
                Assert.True(leader.IsLeader);
                Assert.True(during.Length >= 1000, $"Only {during.Length} HTTP samples overlapped maintenance.");
                var duringRows = during.Where(sample => sample.DuringRows).ToArray();
                Assert.True(duringRows.Length >= 200, $"Only {duringRows.Length} HTTP samples overlapped row deletion.");
                var before = P95(baseline);
                var overall = P95(during);
                var deleting = P95(duringRows);
                output.WriteLine($"Actual PG17 + filesystem + renewing one-second engine fence + RSA/JWT Kestrel, " +
                    $"{ExpiredCount} expired full-graph runs, 150 retained + 4 live, 4 lanes at 50 requests/s each. " +
                    $"Baseline n={baseline.Length}, p95={before:F3}ms; maintenance n={during.Length}, p95={overall:F3}ms " +
                    $"({(overall / before - 1) * 100:F2}%); row deletion n={duringRows.Length}, p95={deleting:F3}ms " +
                    $"({(deleting / before - 1) * 100:F2}%). No latency floor, outlier removal, retries or mocked HTTP.");
                string[] runTables = ["runs", "run_events", "run_snapshots", "node_runs", "task_attempts", "reports", "artifacts", "artifact_grants"];
                foreach (var table in runTables)
                    Assert.Equal(0, await Count($"SELECT count(*) FROM {table} WHERE {(table == "runs" ? "id" : "run_id")}=ANY($1)", expired.Runs));
                Assert.Equal(0, await Count("SELECT count(*) FROM report_ids WHERE id=ANY($1)", expired.Runs));
                Assert.Equal(0, await Count("SELECT count(*) FROM retention_blob_deletions"));
                foreach (var content in expired.Blobs)
                    await Assert.ThrowsAnyAsync<IOException>(() => blobs.DownloadAsync(content, Stream.Null, Token));
                foreach (var keep in retained)
                {
                    foreach (var table in runTables)
                        Assert.Equal(keep.Runs.Length, await Count(
                            $"SELECT count(*) FROM {table} WHERE {(table == "runs" ? "id" : "run_id")}=ANY($1)", keep.Runs));
                    Assert.Equal(keep.Runs.Length, await Count("SELECT count(*) FROM report_ids WHERE id=ANY($1)", keep.Runs));
                    foreach (var content in keep.Blobs) await blobs.DownloadAsync(content, Stream.Null, Token);
                }
                Assert.Equal(live.Length, await Count("SELECT count(*) FROM runs WHERE id=ANY($1)", live));
                Assert.True(overall < before * 1.10,
                    $"Live API maintenance p95 increase {(overall / before - 1) * 100:F2}% must be strictly below 10%.");
                Assert.True(deleting < before * 1.10,
                    $"Live API row-purge p95 increase {(deleting / before - 1) * 100:F2}% must be strictly below 10%.");

                async Task Maintain()
                {
                    await start.Task.WaitAsync(Token);
                    Assert.Equal(ExpiredCount, await purger.PurgeBatchAsync(Token));
                    rows.SetResult();
                    while (await purger.DeleteBlobBatchAsync(Token) != 0) { }
                    Assert.Equal(0, await purger.PurgeBatchAsync(Token));
                    await partitions.DropExpiredEmptyAsync(Token);
                }
            }
            finally { await leader.StopAsync(CancellationToken.None); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private async Task<Sample[]> Measure(MetricsApiServer api, string jwt, IReadOnlyList<Request> requests,
        int? perLane, Task? maintenance, Task? rows)
    {
        var started = Stopwatch.GetTimestamp();
        var lanes = Enumerable.Range(0, Lanes).Select(async lane =>
        {
            var samples = new List<Sample>();
            for (var index = 0; perLane is not null ? index < perLane : !maintenance!.IsCompleted; index++)
            {
                var due = TimeSpan.FromMilliseconds(index * 20 + lane * 5);
                var delay = due - Stopwatch.GetElapsedTime(started);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, Token);
                if (maintenance?.IsCompleted == true) break;
                var request = requests[(index * Lanes + lane) % requests.Count];
                var duringRows = rows?.IsCompleted == false;
                var sent = Stopwatch.GetTimestamp();
                using var response = await api.Get(request.Path, jwt, Token);
                var body = await response.Content.ReadAsStringAsync(Token);
                var elapsed = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.Equal(request.Expected, body);
                samples.Add(new(elapsed, duringRows));
            }
            return samples.ToArray();
        }).ToArray();
        return (await Task.WhenAll(lanes)).SelectMany(samples => samples).ToArray();
    }

    private static double P95(Sample[] samples) =>
        samples.Select(sample => sample.Milliseconds).Order().ElementAt((int)Math.Ceiling(samples.Length * 0.95) - 1);

    private async Task<(string Team, Guid Group)> Registry()
    {
        var team = "retention-load-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team, [new(team, "short", "Short retention", new([])), new(team, "normal", "Default retention", new([])),
                new(team, "long", "Long retention", new([]))], RoleBindings: [new(RegistryRole.Viewer, group)]), Token);
        await transaction.CommitAsync(Token);
        await Execute("UPDATE workflow_types SET retention_days=14 WHERE id=$1", team + "/short");
        await Execute("UPDATE workflow_types SET retention_days=180 WHERE id=$1", team + "/long");
        await Execute("INSERT INTO workflows(id,type_id) SELECT id||'/workflow',id FROM workflow_types WHERE team_id=$1", team);
        return (team, group);
    }

    private async Task<Guid[]> LiveRuns(string team)
    {
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var runs = new List<Guid>();
        for (var index = 0; index < Lanes; index++)
        {
            var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
                new("retention-load", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
            await store.CreateAsync(run, team + "/short/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
            await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
            foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                await store.AppendAsync(run, EventEntity.Task, "node/task", (int)status, DateTimeOffset.UtcNow, Token);
            runs.Add(run.Snapshot.RunId);
        }
        return runs.ToArray();
    }

    private async Task<Fixture> Seed(string workflow, DateTimeOffset at, int count, FileSystemBlobStore blobs)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7(at)).ToArray();
        await Execute("SELECT ensure_run_partitions($1)", DateOnly.FromDateTime(at.UtcDateTime));
        await Execute("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,ended_at,deadline_at)
            SELECT uuid_v7_run_day(id),id,$2,$3,0,'retention-load','{}','{}',3,$4,$4+interval '1 minute',$4+interval '1 day'
            FROM unnest($1::uuid[]) id
            """, ids, workflow, Guid.CreateVersion7(), at);
        await Execute("""
            INSERT INTO node_runs(run_day,run_id,node_key,status)
            SELECT uuid_v7_run_day(id),id,'node',5 FROM unnest($1::uuid[]) id
            """, ids);
        await Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at)
            SELECT uuid_v7_run_day(id),id,'node/task',1,5,$2+interval '1 day' FROM unnest($1::uuid[]) id
            """, ids, at);
        await Execute("""
            INSERT INTO run_events(run_day,run_id,seq,ts,kind,payload)
            SELECT uuid_v7_run_day(id),id,1,$2,0,''::bytea FROM unnest($1::uuid[]) id
            """, ids, at);
        await Execute("""
            INSERT INTO run_snapshots(run_day,run_id,seq,created_at,payload)
            SELECT uuid_v7_run_day(id),id,0,$2,''::bytea FROM unnest($1::uuid[]) id
            """, ids, at);
        var contents = new List<BlobContent>();
        var hashes = new List<string>();
        foreach (var run in ids)
        {
            var bytes = Encoding.UTF8.GetBytes(run.ToString("D"));
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            hashes.Add(hash);
            foreach (var content in new[]
            {
                new BlobContent(BlobKind.Logs, hash, bytes.Length, $"logs/{run:D}/{hash}/1/000001.log.gz"),
                new BlobContent(BlobKind.Reports, hash, bytes.Length, $"reports/{run:D}/{run:D}"),
                new BlobContent(BlobKind.Resources, hash, bytes.Length, $"resources/{run:D}/{hash}/1/1-{hash}.json"),
                new BlobContent(BlobKind.Artifacts, hash, bytes.Length)
            })
            {
                using var input = new MemoryStream(bytes);
                await blobs.UploadAsync(content, input, Token);
                contents.Add(content);
            }
        }
        await Execute("""
            INSERT INTO report_ids(id,run_day) SELECT id,uuid_v7_run_day(id) FROM unnest($1::uuid[]) id
            """, ids);
        await Execute("""
            INSERT INTO reports(run_day,id,run_id,source_key,attempt,kind,title,tags,size_bytes,blob_uri,sha256,created_at)
            SELECT uuid_v7_run_day(id),id,id,'node/task',1,'text','Retention load','{}',36,
                'reports/'||id::text||'/'||id::text,decode(hash,'hex'),$3 FROM unnest($1::uuid[],$2::text[]) AS fixture(id,hash)
            """, ids, hashes.ToArray(), at);
        await Execute("""
            INSERT INTO artifact_grants(id,run_day,run_id,source_key,attempt,agent_id,session_id,reservation_epoch,
                name,content_type,size_bytes,sha256,expires_at,committed)
            SELECT id,uuid_v7_run_day(id),id,'node/task',1,$4,$5,1,'out.txt','text/plain',36,decode(hash,'hex'),$3,true
            FROM unnest($1::uuid[],$2::text[]) AS fixture(id,hash)
            """, ids, hashes.ToArray(), at.AddDays(1), Guid.NewGuid(), Guid.NewGuid());
        await Execute("""
            INSERT INTO artifacts(run_day,id,run_id,source_key,name,content_type,size_bytes,blob_uri,sha256,created_at)
            SELECT uuid_v7_run_day(id),id,id,'node/task','out.txt','text/plain',36,'artifacts/'||hash,decode(hash,'hex'),$3
            FROM unnest($1::uuid[],$2::text[]) AS fixture(id,hash)
            """, ids, hashes.ToArray(), at);
        await Execute("SELECT refresh_daily_rollups(uuid_v7_run_day($1),$2)", ids[0], workflow);
        return new(ids, contents.ToArray());
    }

    private Task Analyze() => Execute("""
        ANALYZE runs; ANALYZE run_events; ANALYZE run_snapshots; ANALYZE node_runs; ANALYZE task_attempts;
        ANALYZE reports; ANALYZE artifacts; ANALYZE workflow_types; ANALYZE workflows;
        """);

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
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 2, 30, 0, TimeSpan.Zero);
    }
}
