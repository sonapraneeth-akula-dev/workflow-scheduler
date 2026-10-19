using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Engine.Scheduler;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Testing.RunQueries;

namespace Wf.Integration.Tests.Auth;

public sealed class RunHistoryTests(AuthorizationDatabase database, ITestOutputHelper output) : IClassFixture<AuthorizationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance);
    private Task<RunQueryServer> Server(bool abort = false) => RunQueryServer.Start(database.DataSource, Token, abort);

    [Fact]
    public async Task RunHistory_real_100k_seed_pages_both_sorts_and_combined_status_metadata_time_series_filters()
    {
        var seed = await RunSeeder.SeedAsync(database.DataSource, Token);
        Assert.Equal(100_000, seed.Rows.Length);
        Assert.Equal(100_000L, await Scalar("SELECT count(*) FROM runs WHERE split_part(workflow_id,'/',1)=ANY($1)", (object)seed.Teams.ToArray()));
        await using var server = await Server();
        var jwt = server.Jwt(seed.ViewerGroup);
        var workflow = seed.Rows[0].WorkflowId;
        var own = seed.Rows.Where(row => row.WorkflowId == workflow).OrderBy(row => row.CreatedAt).ToArray();
        Assert.Equal(500, own.Length);
        foreach (var sort in new[] { "createdAtAsc", "createdAtDesc" })
        {
            var path = "/api/v1/runs/history?workflowId=" + workflow + "&limit=37&sort=" + sort;
            var actual = await All(server, path, jwt);
            Assert.Equal(sort == "createdAtAsc" ? own.Select(row => row.Id) : own.Reverse().Select(row => row.Id),
                actual.Select(row => row.RunId!.Value));
            Assert.All(actual, row =>
            {
                Assert.Equal("Run", row.Kind);
                Assert.Equal(workflow, row.WorkflowId);
                Assert.Empty(row.Variables);
                Assert.Null(row.Abort); // Seeded status alone never invents an abort actor or reason.
                if (row.EndedAt is not null) Assert.Equal(1000d, row.DurationMs);
            });
        }
        var candidate = own.First(row => row.Status == RunStatus.Aborted);
        var start = own[40].CreatedAt;
        var end = own[^40].CreatedAt;
        var query = "/api/v1/series/" + candidate.SeriesId + "/history?status=Aborted&meta.environment=" +
            candidate.Environment + "&meta.region=" + candidate.Region + "&from=" + Uri.EscapeDataString(start.ToString("O")) +
            "&to=" + Uri.EscapeDataString(end.ToString("O")) + "&limit=11&sort=createdAtAsc";
        var expected = own.Where(row => row.Status == RunStatus.Aborted && row.Environment == candidate.Environment &&
            row.Region == candidate.Region && row.CreatedAt >= start && row.CreatedAt < end).ToArray();
        Assert.NotEmpty(expected);
        var filtered = await All(server, query, jwt);
        Assert.Equal(expected.Select(row => row.Id), filtered.Select(row => row.RunId!.Value));
        Assert.All(filtered, row => Assert.Equal(candidate.Environment, row.Metadata!.Values["environment"]));
        var record = new AbortRecord(Guid.NewGuid().ToString(), "Seeded operator", candidate.CreatedAt.AddSeconds(2),
            "100k durable abort fact", AbortSource.API);
        var change = new RunEvent(candidate.Id, 1, record.At, EventEntity.Run, null, (int)RunStatus.Aborted, record);
        await Execute("""
            INSERT INTO run_events(run_day,run_id,seq,ts,kind,payload)
            VALUES(uuid_v7_run_day($1),$1,1,$2,0,$3)
            """, candidate.Id, record.At, JsonSerializer.SerializeToUtf8Bytes(change));
        var byActor = Assert.Single(await All(server, "/api/v1/runs/history?workflowId=" + workflow +
            "&status=Aborted&abortedBy=" + record.By, jwt));
        Assert.Equal(candidate.Id, byActor.RunId);
        Assert.Equal(record.By, byActor.Abort!.By);
        Assert.Equal(record.Reason, byActor.Abort.Reason);
        Assert.Equal(record.At, DateTimeOffset.Parse(byActor.Abort.At, System.Globalization.CultureInfo.InvariantCulture));
        using var foreign = await server.Get("/api/v1/runs/history?workflowId=" + seed.Rows[1].WorkflowId
            .Replace(seed.Teams[0], seed.Teams[1], StringComparison.Ordinal), jwt);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        output.WriteLine("RunHistory actual PostgreSQL RunSeeder rows=100000; own workflow=500; both sorts traversed 500 unique rows; combined filters and committed abort by/at/reason exact.");
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, false)]
    [InlineData(RegistryRole.Operator, false)]
    [InlineData(RegistryRole.Author, false)]
    [InlineData(RegistryRole.Admin, false)]
    [InlineData(RegistryRole.Viewer, true)]
    [InlineData(RegistryRole.Operator, true)]
    [InlineData(RegistryRole.Author, true)]
    [InlineData(RegistryRole.Admin, true)]
    public async Task RunHistory_signed_JWT_requires_fresh_stored_team_or_type_Viewer_scope(RegistryRole role, bool teamScope)
    {
        var seed = await Seed(role, teamScope);
        var run = await Create(seed.Workflow);
        await Create(seed.Team + "/sibling/workflow");
        var foreign = await Create(seed.Team + "-foreign/release/workflow");
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group);
        var path = "/api/v1/workflows/" + seed.Workflow + "/history";
        Assert.Equal(run.Snapshot.RunId, Assert.Single(await All(server, path, jwt)).RunId);
        using (var sibling = await server.Get("/api/v1/workflows/" + seed.Team + "/sibling/workflow/history", jwt))
            Assert.Equal(teamScope ? HttpStatusCode.OK : HttpStatusCode.Forbidden, sibling.StatusCode);
        foreach (var token in new[]
        {
            server.Jwt(seed.Group, identity: "app", scope: null, role: "Registrar"),
            server.Jwt(seed.Group, identity: "app", scope: null, role: "Agent"),
            server.Jwt(seed.Group, extra: [new("hasgroups", "true")]),
            server.Jwt(seed.Group, extra: [new("groups", "not-a-guid")])
        })
        {
            using var denied = await server.Get(path, token);
            await Error(denied, HttpStatusCode.Forbidden, "insufficient_scope_role");
        }
        using (var denied = await server.Get("/api/v1/runs/history?workflowId=" + seed.Team + "-foreign/release/workflow", jwt))
            await Error(denied, HttpStatusCode.Forbidden, "insufficient_scope_role");
        using (var unknown = await server.Get("/api/v1/runs/history?workflowId=" + seed.Team + "/release/missing", server.Jwt(role: "Admin")))
            await Error(unknown, HttpStatusCode.Forbidden, "insufficient_scope_role");
        using (var anonymous = await server.Get(path, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.True(anonymous.Headers.CacheControl!.NoStore);
        }
        using (var tampered = await server.Get(path, jwt[..^3] + "xxx")) Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        using var revoked = await server.Get(path, jwt);
        await Error(revoked, HttpStatusCode.Forbidden, "insufficient_scope_role");
        Assert.NotEqual(run.Snapshot.RunId, foreign.Snapshot.RunId);
    }

    [Fact]
    public async Task RunHistory_abort_reads_the_first_committed_event_not_status_and_omits_private_variable_values()
    {
        var seed = await Seed(RegistryRole.Operator);
        var version = Guid.CreateVersion7();
        await Version(seed, version);
        using var values = JsonDocument.Parse("""{"environment":"private-env","count":23,"token":{"name":"private-secret-reference"},"blob":"blob://private-capability"}""");
        var run = await Create(seed.Workflow, version, values.RootElement.EnumerateObject()
            .ToDictionary(pair => pair.Name, pair => pair.Value));
        await using var server = await Server(abort: true);
        var jwt = server.Jwt(seed.Group, extra: [new("name", "First operator")]);
        using var accepted = await server.Post("/api/v1/runs/" + run.Snapshot.RunId + "/abort", jwt,
            JsonContent.Create(new AbortRunRequest("Original history reason"), AbortRunJson.Default.AbortRunRequest));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var original = (await accepted.Content.ReadFromJsonAsync(AbortRunJson.Default.AbortRunResponse, Token))!;
        using var repeat = await server.Post("/api/v1/runs/" + run.Snapshot.RunId + "/abort", server.Jwt(seed.Group),
            JsonContent.Create(new AbortRunRequest("Not the accepted reason"), AbortRunJson.Default.AbortRunRequest));
        Assert.Equal(original, await repeat.Content.ReadFromJsonAsync(AbortRunJson.Default.AbortRunResponse, Token));
        var path = "/api/v1/workflows/" + seed.Workflow + "/history";
        var pending = Assert.Single(await All(server, path, jwt));
        Assert.Null(pending.Abort); // Acceptance is not yet an engine-committed abort event.
        var record = new AbortRecord(original.By, original.ByName, original.At, original.Reason, original.Source);
        await Store.AppendAsync(run, run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Aborted, record.At, record), Token);
        var legacy = await Create(seed.Workflow, version);
        await Store.AppendAsync(legacy, EventEntity.Run, null, (int)RunStatus.Aborted, DateTimeOffset.UtcNow, Token);
        var aborted = await All(server, path + "?status=Aborted&abortedBy=" + original.By, jwt);
        var row = Assert.Single(aborted);
        Assert.Equal(run.Snapshot.RunId, row.RunId);
        Assert.Equal(version, row.VersionId);
        Assert.Equal("1.0.0", row.Version);
        Assert.Equal("Aborted", row.Status);
        Assert.Equal(record.By, row.Abort!.By);
        Assert.Equal(record.ByName, row.Abort.ByName);
        Assert.Equal(record.At, DateTimeOffset.Parse(row.Abort.At, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(record.Reason, row.Abort.Reason);
        Assert.Equal("API", row.Abort.Source);
        Assert.Equal(["count", "environment", "token"], row.Variables.Select(variable => variable.Key));
        Assert.True(row.Variables.Single(variable => variable.Key == "token").Redacted);
        using var response = await server.Get(path, jwt);
        var json = await response.Content.ReadAsStringAsync(Token);
        foreach (var forbidden in new[] { "private-env", "private-secret-reference", "blob://", "private-capability", "private-default", "arguments", "packageUri", "sourceUri" })
            Assert.DoesNotContain(forbidden, json);
        var both = await All(server, path + "?status=Aborted", jwt);
        Assert.Equal(2, both.Count);
        Assert.Null(both.Single(item => item.RunId == legacy.Snapshot.RunId).Abort);
        Assert.Empty(await All(server, path + "?status=Aborted&abortedBy=" + Guid.NewGuid(), jwt));
        // Editing today's metadata cannot change either run's copied public metadata.
        await Execute("""UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{metadata,values}','{"env":"today"}') WHERE id=$1""", version);
        using var searchResponse = await server.Get("/api/v1/runs?workflowId=" + seed.Workflow, jwt);
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var search = (await searchResponse.Content.ReadFromJsonAsync(RunQueryJsonContext.Default.RunQueryPage, Token))!;
        Assert.All(search.Items, item => Assert.Equal("historical", item.Metadata!.Values["env"]));
        var searchJson = await searchResponse.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("private-env", searchJson);
        Assert.DoesNotContain("variables", searchJson);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-rc.1+build.7")]
    public async Task RunHistory_shows_the_executed_semver_not_the_new_active_version_even_after_deletion(string semver)
    {
        var seed = await Seed();
        var original = Guid.CreateVersion7();
        await Version(seed, original, semver);
        var run = await Create(seed.Workflow, original);
        var series = Guid.CreateVersion7();
        await Execute("UPDATE runs SET series_id=$2 WHERE id=$1", run.Snapshot.RunId, series);
        var latest = Guid.CreateVersion7();
        await Version(seed, latest, "9.0.0");
        await Execute("UPDATE workflows SET active_version_id=$2,latest_version_id=$2,deleted_at=now() WHERE id=$1", seed.Workflow, latest);
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group);
        foreach (var path in new[]
        {
            "/api/v1/workflows/" + seed.Workflow + "/history",
            "/api/v1/runs/history?workflowId=" + seed.Workflow,
            "/api/v1/series/" + series + "/history"
        })
        {
            var row = Assert.Single(await All(server, path, jwt));
            Assert.Equal(run.Snapshot.RunId, row.RunId);
            Assert.Equal(original, row.VersionId);
            Assert.Equal(semver, row.Version);
        }
    }

    [Fact]
    public async Task RunHistory_retained_runs_without_a_version_registration_do_not_invent_a_semver()
    {
        var seed = await Seed();
        var version = Guid.CreateVersion7();
        var run = await Create(seed.Workflow, version);
        await using var server = await Server();
        var row = Assert.Single(await All(server, "/api/v1/workflows/" + seed.Workflow + "/history", server.Jwt(seed.Group)));
        Assert.Equal(version, row.VersionId);
        Assert.Equal(run.Snapshot.RunId, row.RunId);
        Assert.Null(row.Version);
    }

    [Fact]
    public async Task RunHistory_actual_scheduler_preserves_skipped_missed_reasons_config_and_deleted_resource_facts()
    {
        var seed = await Seed();
        var run = await Create(seed.Workflow);
        var schedule = Guid.CreateVersion7();
        var series = Guid.CreateVersion7();
        var config = Guid.CreateVersion7();
        var at = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        await Execute("""
            INSERT INTO workflows(id,type_id) VALUES($1,$2);
            """, seed.Workflow, seed.Team + "/release");
        await Execute("""
            INSERT INTO schedule_config_versions(id,workflow_id,source_path,git_commit,content_sha256,git_author,registered_by)
            VALUES($1,$2,'history.schedules.json',repeat('a',40),decode(repeat('00',32),'hex'),'history','history')
            """, config, seed.Workflow);
        await Execute("""
            INSERT INTO schedules(id,workflow_id,origin,code_key,name,timezone,interval_s,catchup_policy,next_fire_at,
                config_version_id,created_by,updated_by)
            VALUES($1,$2,0,'history','History','Etc/UTC',60,0,$3,$4,'history','history')
            """, schedule, seed.Workflow, at.AddMinutes(-10), config);
        await Execute("INSERT INTO run_series(id,workflow_id,schedule_id) VALUES($1,$2,$3)", series, seed.Workflow, schedule);
        await Execute("UPDATE runs SET series_id=$2 WHERE id=$1", run.Snapshot.RunId, series);
        var clock = new Clock(at);
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"), new("http://localhost:5200")),
            NullLogger<LeaseManager>.Instance);
        await Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        using var versions = new ActiveVersionCache(database.DataSource, NullLogger<ActiveVersionCache>.Instance, clock);
        using var recovery = new RunRecovery(database.DataSource, Store, leader, clock, NullLogger<RunRecovery>.Instance);
        using var scheduler = new ScheduleFireService(database.DataSource, leader, recovery, versions,
            new ScheduleCatchUp(clock), clock, NullLogger<ScheduleFireService>.Instance, new("prod"));
        await scheduler.FireAsync(new(schedule, at.AddMinutes(-10)), Token);
        await using var server = await Server();
        var path = "/api/v1/series/" + series + "/history?sort=createdAtAsc&limit=3";
        var jwt = server.Jwt(seed.Group);
        using (var context = await server.Get(path, jwt)) Assert.Equal(schedule, (await Page(context)).CurrentScheduleId);
        using (var context = await server.Get(path + "&status=Aborted", jwt))
        {
            var empty = await Page(context);
            Assert.Empty(empty.Items);
            Assert.Equal(schedule, empty.CurrentScheduleId);
        }
        var facts = await All(server, path, jwt);
        var fires = facts.Where(row => row.Kind == "Fire").ToArray();
        Assert.Equal(11, fires.Length);
        Assert.Equal(10, fires.Count(row => row.Fire!.Outcome == "Missed"));
        Assert.All(fires.Where(row => row.Fire!.Outcome == "Missed"), row => Assert.Equal("missed-grace", row.Fire!.Reason));
        Assert.Equal("overlap", Assert.Single(fires, row => row.Fire!.Outcome == "Skipped").Fire!.Reason);
        Assert.All(fires, row =>
        {
            Assert.Null(row.RunId);
            Assert.Null(row.VersionId);
            Assert.Null(row.Version);
            Assert.Null(row.Status);
            Assert.Empty(row.Variables);
            Assert.Equal(config, row.Fire!.ScheduleConfigVersionId);
        });
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", series));
        Assert.Equal(10, (await All(server, "/api/v1/series/" + series + "/history?outcome=Missed", jwt)).Count);
        Assert.Single(await All(server, "/api/v1/series/" + series + "/history?outcome=Skipped", jwt));
        Assert.Single(await All(server, "/api/v1/series/" + series + "/history?status=Queued", jwt));
        await Execute("UPDATE schedules SET deleted_at=now(),paused=true,next_fire_at=NULL,config_version_id=NULL WHERE id=$1", schedule);
        await Execute("UPDATE workflows SET deleted_at=now() WHERE id=$1", seed.Workflow);
        using (var context = await server.Get(path, jwt)) Assert.Null((await Page(context)).CurrentScheduleId);
        var retained = await All(server, path, jwt);
        Assert.Equal(facts.Select(row => (row.Kind, row.At, row.RunId, row.Fire)), retained.Select(row => (row.Kind, row.At, row.RunId, row.Fire)));
        var workflowHistory = await All(server, "/api/v1/workflows/" + seed.Workflow + "/history", jwt);
        Assert.Equal(facts.Count, workflowHistory.Count);
        await Execute("DELETE FROM workflows WHERE id=$1", seed.Workflow);
        Assert.Equal(facts.Count, (await All(server, "/api/v1/workflows/" + seed.Workflow + "/history", jwt)).Count);
        // Old unrecorded provenance/reasons remain unknown rather than being invented from today's schedule.
        await Execute("INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,$2,2)", schedule, at.AddDays(-1));
        var legacy = Assert.Single(await All(server, "/api/v1/series/" + series + "/history?outcome=Missed", jwt),
            row => row.At == at.AddDays(-1));
        Assert.Null(legacy.Fire!.Reason);
        Assert.Null(legacy.Fire.ScheduleConfigVersionId);
        output.WriteLine("Real scheduler durable history: 10 Missed/missed-grace + 1 Skipped/overlap, zero extra runs; captured config retained after deletion/edit.");
    }

    [Fact]
    public async Task RunHistory_keyset_ties_insert_stability_filter_binding_revocation_and_ambiguous_series_fail_closed()
    {
        var seed = await Seed();
        var series = Guid.CreateVersion7();
        var at = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        var runs = new List<Guid>();
        foreach (var index in Enumerable.Range(0, 5))
        {
            var run = await Create(seed.Workflow);
            runs.Add(run.Snapshot.RunId);
            await Execute("UPDATE runs SET created_at=$2,series_id=$3 WHERE id=$1", run.Snapshot.RunId, at, series);
        }
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group);
        var path = "/api/v1/runs/history?workflowId=" + seed.Workflow + "&limit=2";
        using var firstResponse = await server.Get(path, jwt);
        var first = await Page(firstResponse);
        Assert.NotNull(first.NextCursor);
        await Create(seed.Workflow);
        var all = first.Items.ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            using var response = await server.Get(path + "&cursor=" + cursor, jwt);
            var page = await Page(response);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        Assert.Equal(runs.OrderDescending(), all.Select(row => row.RunId!.Value));
        foreach (var changed in new[] { "&sort=createdAtAsc", "&status=Queued", "&outcome=Run", "&abortedBy=" + Guid.NewGuid() })
        {
            using var response = await server.Get(path + changed + "&cursor=" + first.NextCursor, jwt);
            await Error(response, HttpStatusCode.BadRequest, "invalid_run_history_cursor");
        }
        var foreign = await Create(seed.Team + "-foreign/release/workflow");
        await Execute("UPDATE runs SET series_id=$2 WHERE id=$1", foreign.Snapshot.RunId, series);
        using (var ambiguous = await server.Get("/api/v1/series/" + series + "/history", server.Jwt(role: "Admin")))
            await Error(ambiguous, HttpStatusCode.Forbidden, "insufficient_scope_role");
        using (var unknown = await server.Get("/api/v1/series/" + Guid.NewGuid() + "/history", server.Jwt(role: "Admin")))
            await Error(unknown, HttpStatusCode.Forbidden, "insufficient_scope_role");
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        using var revoked = await server.Get(path + "&cursor=" + first.NextCursor, jwt);
        await Error(revoked, HttpStatusCode.Forbidden, "insufficient_scope_role");
    }

    [Fact]
    public async Task RunHistory_mixed_run_and_fire_same_timestamp_and_UUID_pages_by_kind_in_both_directions()
    {
        var seed = await Seed();
        var run = await Create(seed.Workflow);
        var id = run.Snapshot.RunId;
        var series = Guid.CreateVersion7();
        var at = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        await Execute("""
            INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
            VALUES($1,$2,1,'Tied history','Etc/UTC',60,'history','history')
            """, id, seed.Workflow);
        await Execute("INSERT INTO run_series(id,workflow_id,schedule_id) VALUES($1,$2,$3)", series, seed.Workflow, id);
        await Execute("UPDATE runs SET created_at=$2,series_id=$3 WHERE id=$1", id, at, series);
        await Execute("INSERT INTO schedule_fires(schedule_id,fire_at,outcome,reason) VALUES($1,$2,2,'missed-grace')", id, at);
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group);
        foreach (var sort in new[] { "createdAtAsc", "createdAtDesc" })
        {
            var rows = await All(server, "/api/v1/series/" + series + "/history?limit=1&sort=" + sort, jwt);
            Assert.Equal(sort == "createdAtAsc" ? ["Run", "Fire"] : new[] { "Fire", "Run" }, rows.Select(row => row.Kind));
            Assert.All(rows, row => Assert.Equal(at, row.At));
        }
    }

    [Fact]
    public async Task RunHistory_corrupt_committed_data_and_database_failure_return_sanitized_no_store_problems()
    {
        var seed = await Seed();
        var run = await Create(seed.Workflow);
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group);
        var path = "/api/v1/workflows/" + seed.Workflow + "/history";
        await Execute("""UPDATE runs SET metadata='{"Type":42,"Values":{"sensitive":"private-value"}}' WHERE id=$1""", run.Snapshot.RunId);
        using (var corrupt = await server.Get(path, jwt))
        {
            await Error(corrupt, HttpStatusCode.InternalServerError, "run_history_data_inconsistent");
            Assert.DoesNotContain("private-value", await corrupt.Content.ReadAsStringAsync(Token));
        }
        await Execute("ALTER TABLE run_events RENAME TO run_history_unavailable_events");
        try
        {
            using var unavailable = await server.Get(path, jwt);
            await Error(unavailable, HttpStatusCode.ServiceUnavailable, "run_history_unavailable");
            var text = await unavailable.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("run_history_unavailable_events", text);
            Assert.DoesNotContain("SELECT", text);
        }
        finally { await Execute("ALTER TABLE run_history_unavailable_events RENAME TO run_events"); }
    }

    [Theory]
    [InlineData("?sort=duration")]
    [InlineData("?cursor=not-a-cursor")]
    [InlineData("?limit=0")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?abortedBy=someone")]
    [InlineData("?status=aborted")]
    [InlineData("?outcome=Started")]
    [InlineData("?workflowId=other/release/override")]
    public async Task RunHistory_invalid_query_is_explicit_sanitized_no_store(string query)
    {
        await using var server = await Server();
        using var response = await server.Get("/api/v1/workflows/team/release/workflow/history" + query, server.Jwt(role: "Admin"));
        await Error(response, HttpStatusCode.BadRequest, "invalid_run_history_query");
        Assert.DoesNotContain("not-a-cursor", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task RunHistory_source_generated_OpenApi_describes_actual_history_and_required_array_contracts()
    {
        await using var server = await Server();
        using var response = await server.Get("/openapi/v1.json", server.Jwt(role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/runs/history", "/api/v1/workflows/{team}/{type}/{workflow}/history", "/api/v1/series/{id}/history" })
        {
            var operation = paths.GetProperty(path).GetProperty("get");
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "abortedBy");
            Assert.Equal("#/components/schemas/RunHistoryPage", operation.GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("array", schemas.GetProperty("RunHistoryRow").GetProperty("properties").GetProperty("variables").GetProperty("type").GetString());
        Assert.False(schemas.GetProperty("RunHistoryVariable").GetProperty("properties").TryGetProperty("value", out _));
        Assert.True(schemas.GetProperty("RunHistoryFire").GetProperty("properties").TryGetProperty("scheduleConfigVersionId", out _));
        Assert.True(schemas.GetProperty("RunHistoryPage").GetProperty("properties").TryGetProperty("currentScheduleId", out _));
        Assert.Contains(schemas.GetProperty("RunHistoryRow").GetProperty("required").EnumerateArray(),
            property => property.GetString() == "version");
        Assert.Contains(schemas.GetProperty("RunQueryRow").GetProperty("required").EnumerateArray(),
            property => property.GetString() == "metadata");
    }

    private async Task<Seeded> Seed(RegistryRole role = RegistryRole.Viewer, bool teamScope = false)
    {
        var team = "history-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        var binding = new RegistryRoleBinding(role, group);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction, new(team,
            [new(team, "release", "history", new([]), teamScope ? [] : [binding]), new(team, "sibling", "history", new([]))],
            RoleBindings: teamScope ? [binding] : []), Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team + "-foreign", [new(team + "-foreign", "release", "history", new([]))]), Token);
        await transaction.CommitAsync(Token);
        return new(team, group);
    }

    private async Task<RunState> Create(string workflow, Guid? version = null, IReadOnlyDictionary<string, JsonElement>? variables = null)
    {
        var run = new RunState(Guid.CreateVersion7(), [],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty.Add("env", "historical")),
            Assert.Fail, variables);
        await Store.CreateAsync(run, workflow, version ?? Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        return run;
    }

    private async Task Version(Seeded seed, Guid version, string semver = "1.0.0")
    {
        await Execute("INSERT INTO workflows(id,type_id) VALUES($1,$2) ON CONFLICT(id) DO NOTHING", seed.Workflow, seed.Team + "/release");
        await Execute("""
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,$2,$3,'{"metadata":{"values":{"env":"old"}},
                "variables":[{"key":"environment","type":"String","default":"private-default"},{"key":"count","type":"Int"},
                {"key":"token","type":"Secret"}]}','packages/'||repeat('00',32)||'.zip',decode(repeat('00',32),'hex'),1,
                'sources/'||repeat('00',32),decode(repeat('00',32),'hex'),1,repeat('a',40),'history','history','1.0.0')
            """, version, seed.Workflow, semver);
    }

    private async Task Execute(string sql, params object[] parameters) => await Scalar(sql, parameters);

    private async Task<object?> Scalar(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) query.Parameters.AddWithValue(parameter);
        return await query.ExecuteScalarAsync(Token);
    }

    private static async Task<List<RunHistoryRow>> All(RunQueryServer server, string path, string jwt)
    {
        var rows = new List<RunHistoryRow>();
        string? cursor = null;
        do
        {
            using var response = await server.Get(path + (cursor is null ? "" : (path.Contains('?') ? "&" : "?") + "cursor=" + cursor), jwt);
            var page = await Page(response);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(rows.Count <= 100_000, "History pagination did not terminate.");
        } while (cursor is not null);
        return rows;
    }

    private static async Task<RunHistoryPage> Page(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync(RunHistoryJsonContext.Default.RunHistoryPage, Token))!;
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(reason, json.RootElement.GetProperty("reason").GetString());
    }

    private sealed record Seeded(string Team, Guid Group)
    {
        public string Workflow => Team + "/release/workflow";
    }

    private sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
