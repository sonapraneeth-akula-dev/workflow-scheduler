using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.ControlPlane.Api;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    public async Task ServiceHistory_projects_only_active_applied_cards_in_file_order_and_preserves_named_series()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var workflow = request.WorkflowId!;
        Assert.Null((await Read()).HistoryCards);
        var content = """
            {"instances":[
              {"id":"training","name":"Training & validation","interval":"00:01:00","timezone":"Etc/UTC"},
              {"id":"test","name":"Full test","interval":"00:02:00","timezone":"Etc/UTC"}],
             "historyCards":[
              {"id":"test-only","name":"Full test","scheduleIds":["test"]},
              {"id":"training-only","name":"Training & validation","scheduleIds":["training"]},
              {"id":"hidden","name":"Hidden","visible":false,"scheduleIds":["training","test"]}]}
            """;
        using var applied = await server.Post(ScheduleRequest(request, content));
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var config = (await Json(applied)).GetProperty("scheduleConfigVersion").GetGuid();
        var first = await Read();
        var cards = Assert.IsType<ServiceHistoryCard[]>(first.HistoryCards);
        Assert.Equal(["test-only", "training-only"], cards.Select(card => card.Id));
        Assert.Equal(["Full test", "Training & validation"], cards.Select(card => card.Name));
        Assert.Equal(2, first.Schedules.Length);
        var training = Assert.Single(first.Schedules, schedule => schedule.Name == "Training & validation");
        var test = Assert.Single(first.Schedules, schedule => schedule.Name == "Full test");
        Assert.Equal([test.SeriesId], cards[0].SeriesIds);
        Assert.Equal([training.SeriesId], cards[1].SeriesIds);
        await Execute("UPDATE schedules SET paused=true WHERE id=$1", training.ScheduleId);
        var due = await Value("SELECT next_fire_at FROM schedules WHERE id=$1", training.ScheduleId);
        var renamed = content.Replace("Training & validation", "Training renamed", StringComparison.Ordinal);
        using var rename = await server.Post(ScheduleRequest(request, renamed));
        Assert.Equal(HttpStatusCode.Created, rename.StatusCode);
        var afterRename = await Read();
        var retained = Assert.Single(afterRename.Schedules, schedule => schedule.SeriesId == training.SeriesId);
        Assert.Equal("Training renamed", retained.Name);
        Assert.True(retained.Paused);
        Assert.Equal(training.ScheduleId, retained.ScheduleId);
        Assert.Equal(due, await Value("SELECT next_fire_at FROM schedules WHERE id=$1", training.ScheduleId));
        var combined = renamed[..renamed.IndexOf("\"historyCards\"", StringComparison.Ordinal)] +
            """
            "historyCards":[{"id":"combined","name":"Combined","scheduleIds":["training","test"]}]}
            """;
        using var grouped = await server.Post(ScheduleRequest(request, combined));
        Assert.Equal(HttpStatusCode.Created, grouped.StatusCode);
        Assert.Equal([training.SeriesId, test.SeriesId], Assert.Single((await Read()).HistoryCards!).SeriesIds);
        using var invalid = await server.Post(ScheduleRequest(request, combined.Replace("[\"training\",\"test\"]",
            "[\"missing\"]", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal([training.SeriesId, test.SeriesId], Assert.Single((await Read()).HistoryCards!).SeriesIds);
        var empty = combined[..combined.IndexOf("\"historyCards\"", StringComparison.Ordinal)] + "\"historyCards\":[]}";
        using var hidden = await server.Post(ScheduleRequest(request, empty));
        Assert.Equal(HttpStatusCode.Created, hidden.StatusCode);
        Assert.Empty((await Read()).HistoryCards!);
        Assert.Equal(2L, await Value("SELECT count(*) FROM schedules WHERE workflow_id=$1 AND deleted_at IS NULL", workflow));
        using var restored = await server.Post(ScheduleRequest(request, content));
        Assert.Equal(HttpStatusCode.Created, restored.StatusCode);
        Assert.Equal(config, await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", workflow));
        var restoredCards = (await Read()).HistoryCards!;
        Assert.Equal(cards.Select(card => (card.Id, card.Name)), restoredCards.Select(card => (card.Id, card.Name)));
        Assert.Equal(cards.SelectMany(card => card.SeriesIds), restoredCards.SelectMany(card => card.SeriesIds));
        using var omitted = await server.Post(ScheduleRequest(request, ScheduleFile()));
        Assert.Equal(HttpStatusCode.Created, omitted.StatusCode);
        var fallback = await Read();
        Assert.Null(fallback.HistoryCards);
        Assert.True(Assert.Single(fallback.Schedules, schedule => schedule.SeriesId == training.SeriesId).Deleted);
        Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", workflow));
        Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));
        Assert.Empty(server.Failures);

        async Task<ServiceWorkflow> Read()
        {
            using var response = await server.Send(HttpMethod.Get, "/api/v1/service-history?team=" + Team(request),
                null!, server.Token("Admin", "user", "wf.read"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Assert.Single((await response.Content.ReadFromJsonAsync(
                ServiceHistoryJson.Default.ServiceHistoryPage, TestContext.Current.CancellationToken))!.Items);
        }
    }

    [Fact]
    public async Task ScheduleFileApplier_schedule_only_returns_only_config_and_preserves_active_workflow_with_exact_provenance()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var content = ScheduleFile() + "\r\n";
        var notified = false;
        await using var listener = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        listener.Notification += (_, args) => notified |= args.Channel == "registry" && args.Payload == request.WorkflowId;
        await using (var listen = new NpgsqlCommand("LISTEN registry", listener))
            await listen.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        using var response = await server.Post(ScheduleRequest(request, content));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("scheduleConfigVersion", Assert.Single(json.EnumerateObject()).Name);
        var config = json.GetProperty("scheduleConfigVersion").GetGuid();
        Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(version, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(config, await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(content, await Value("SELECT content FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(content)),
            Assert.IsType<byte[]>(await Value("SELECT content_sha256 FROM schedule_config_versions WHERE id=$1", config)));
        Assert.Equal("workflows/deploy.schedules.json", await Value("SELECT source_path FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal(request.GitCommit, await Value("SELECT git_commit FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal(request.GitAuthor, await Value("SELECT git_author FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal(Server.Actor.ToString(), await Value("SELECT registered_by FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal(1L, await Value("SELECT count(*) FROM run_series WHERE workflow_id=$1", request.WorkflowId!));
        var summary = Assert.IsType<string>(await Value(
            "SELECT summary::text FROM audit_log WHERE target_id=$1 ORDER BY id DESC LIMIT 1", request.WorkflowId!));
        Assert.Contains(config.ToString(), summary);
        Assert.DoesNotContain(content, summary);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!notified) await listener.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ScheduleFileApplier_combined_registration_returns_all_changed_pointers_once()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = (await Request(server)) with
        {
            Schedules = ScheduleFile(), ScheduleSourcePath = "schedules.json"
        };
        using var response = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["registrySnapshot", "scheduleConfigVersion", "workflowVersion"],
            (await Json(response)).EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task ScheduleFileApplier_identical_content_is_noop_without_rewrites_audit_or_duplicate_series()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var file = ScheduleRequest(request, ScheduleFile());
        using var first = await server.Post(file);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var config = (await Json(first)).GetProperty("scheduleConfigVersion").GetGuid();
        var row = await Value("SELECT xmin::text FROM schedules WHERE workflow_id=$1", request.WorkflowId!);
        var snapshot = await Value("SELECT xmin::text FROM schedule_config_versions WHERE id=$1", config);
        using var again = await server.Post(file with { GitAuthor = "New author", GitCommit = new('b', 40) });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty((await Json(again)).EnumerateObject());
        Assert.Equal(row, await Value("SELECT xmin::text FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(snapshot, await Value("SELECT xmin::text FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal(1L, await Value("SELECT count(*) FROM run_series WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(2L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task ScheduleFileApplier_modify_remove_readd_retains_ids_series_fires_history_and_ui_rows()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        using var first = await server.Post(ScheduleRequest(request, ScheduleFile()));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = Assert.IsType<Guid>(await Value("SELECT id FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        var series = await Value("SELECT id FROM run_series WHERE schedule_id=$1", id);
        var firstConfig = (await Json(first)).GetProperty("scheduleConfigVersion").GetGuid();
        var fire = server.Clock.Now.AddMinutes(-5);
        await Execute("INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,$2,1)", id, fire);
        var run = Guid.CreateVersion7();
        var version = await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!);
        await Execute("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,1,'test','{}','{}',0,now())
            """, run, request.WorkflowId!, version!, series!);
        var user = Guid.CreateVersion7();
        await Execute("""
            INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
            VALUES($1,$2,1,'User schedule','Etc/UTC',90,'test','test')
            """, user, request.WorkflowId!);
        var userBefore = await Value("SELECT xmin::text FROM schedules WHERE id=$1", user);
        using var changed = await server.Post(ScheduleRequest(request, ScheduleFile(120)));
        Assert.Equal(HttpStatusCode.Created, changed.StatusCode);
        Assert.Equal(id, await Value("SELECT id FROM schedules WHERE workflow_id=$1 AND origin=0", request.WorkflowId!));
        Assert.Equal(120, await Value("SELECT interval_s FROM schedules WHERE id=$1", id));
        using var removed = await server.Post(ScheduleRequest(request, """{"instances":[]}"""));
        Assert.Equal(HttpStatusCode.Created, removed.StatusCode);
        Assert.Equal(true, await Value("SELECT deleted_at IS NOT NULL AND paused AND next_fire_at IS NULL FROM schedules WHERE id=$1", id));
        using var readded = await server.Post(ScheduleRequest(request, ScheduleFile()));
        Assert.Equal(HttpStatusCode.Created, readded.StatusCode);
        Assert.Equal(firstConfig, (await Json(readded)).GetProperty("scheduleConfigVersion").GetGuid());
        Assert.Equal(firstConfig, await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(true, await Value("SELECT deleted_at IS NULL AND NOT paused AND interval_s=60 AND row_version=4 FROM schedules WHERE id=$1", id));
        Assert.Equal(series, await Value("SELECT id FROM run_series WHERE schedule_id=$1", id));
        Assert.Equal(1L, await Value("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", id));
        Assert.Equal(series, await Value("SELECT series_id FROM runs WHERE id=$1", run));
        Assert.Equal(userBefore, await Value("SELECT xmin::text FROM schedules WHERE id=$1", user));
        Assert.Equal(3L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task ScheduleFileApplier_unchanged_expression_preserves_due_cursor_and_pause_but_expression_edit_recomputes()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        using var first = await server.Post(ScheduleRequest(request, ScheduleFile()));
        var id = Assert.IsType<Guid>(await Value("SELECT id FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        var due = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        await Execute("UPDATE schedules SET paused=true,next_fire_at=$2 WHERE id=$1", id, due);
        server.Clock.Now = due.AddHours(1);
        using var whitespace = await server.Post(ScheduleRequest(request, ScheduleFile() + "\n"));
        Assert.Equal(HttpStatusCode.Created, whitespace.StatusCode);
        Assert.Equal(due.UtcDateTime, await Value("SELECT next_fire_at FROM schedules WHERE id=$1", id));
        Assert.Equal(true, await Value("SELECT paused FROM schedules WHERE id=$1", id));
        using var changed = await server.Post(ScheduleRequest(request, ScheduleFile(120)));
        Assert.Equal(HttpStatusCode.Created, changed.StatusCode);
        Assert.Equal(server.Clock.Now.AddSeconds(120).UtcDateTime, await Value("SELECT next_fire_at FROM schedules WHERE id=$1", id));
        Assert.Equal(true, await Value("SELECT paused FROM schedules WHERE id=$1", id));
    }

    [Fact]
    public async Task ScheduleFileApplier_new_workflow_defaults_rebind_retained_file_without_new_config()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        request = request with { Manifest = ScheduleManifest(request.WorkflowId!, "1.0.0", 1) };
        using var registered = await server.Post(request);
        using var applied = await server.Post(ScheduleRequest(request, ScheduleFile()));
        var config = (await Json(applied)).GetProperty("scheduleConfigVersion").GetGuid();
        var due = await Value("SELECT next_fire_at FROM schedules WHERE workflow_id=$1", request.WorkflowId!);
        using var next = await server.Post(request with { Manifest = ScheduleManifest(request.WorkflowId!, "2.0.0", 2) });
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        Assert.Equal("workflowVersion", Assert.Single((await Json(next)).EnumerateObject()).Name);
        Assert.Equal(2, await Value("SELECT (variables->>'count')::int FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(2, await Value("SELECT row_version FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(config, await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(due, await Value("SELECT next_fire_at FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(1L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task ScheduleFileApplier_incompatible_retained_file_rolls_back_new_workflow_and_registry()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        request = request with { Manifest = ScheduleManifest(request.WorkflowId!, "1.0.0", 1) };
        using var registered = await server.Post(request);
        var original = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        using var applied = await server.Post(ScheduleRequest(request, ScheduleFile(variables: """{"count":3}""")));
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var config = (await Json(applied)).GetProperty("scheduleConfigVersion").GetGuid();
        using var next = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "2.0.0") });
        Assert.Equal(HttpStatusCode.BadRequest, next.StatusCode);
        Assert.Contains("WFV013", await next.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(config, await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(3, await Value("SELECT (variables->>'count')::int FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(2L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
    }

    [Theory]
    [InlineData("not-json", "WFV011")]
    [InlineData("""{"instances":[{"id":"x","timezone":"Etc/UTC","interval":"00:00:00.5"}]}""", "WFV012")]
    [InlineData("""{"instances":[{"id":"x","timezone":"Etc/UTC","interval":"24855.03:14:08"}]}""", "WFV012")]
    [InlineData("""{"instances":[{"id":"x","timezone":"Etc/UTC","interval":"00:01:00","variables":{"secret":"DO-NOT-LEAK"}}]}""", "WFV013")]
    [InlineData("""{"instances":[{"id":"x","timezone":"Etc/UTC","interval":"00:01:00"},{"id":"x","timezone":"Etc/UTC","interval":"00:02:00"}]}""", "WFV014")]
    [InlineData("""{"instances":[{"id":"x","timezone":"Etc/UTC","interval":"00:01:00"}],"historyCards":[{"id":"x","name":"X","scheduleIds":["missing"]}]}""", "WFV014")]
    [InlineData("""{"instances":[],"historyCards":null}""", "WFV011")]
    public async Task ScheduleFileApplier_invalid_file_rolls_back_combined_registration(string content, string rule)
    {
        await using var server = await Server.Start(database.DataSource);
        var request = (await Request(server)) with { Schedules = content, ScheduleSourcePath = "schedules.json" };
        using var response = await server.Post(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(rule, body);
        Assert.DoesNotContain("DO-NOT-LEAK", body);
        Assert.Equal(0L, await Value("SELECT count(*) FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(0L, await Value("SELECT count(*) FROM teams WHERE id=$1", Team(request)));
        Assert.Equal(0L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(0L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task ScheduleFileApplier_concurrent_identical_applies_create_one_config_schedule_series_and_audit()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => server.Post(ScheduleRequest(request, ScheduleFile()))));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(11, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(1L, await Value("SELECT count(*) FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(1L, await Value("SELECT count(*) FROM run_series WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(2L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
            Assert.Empty(server.Failures);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ScheduleFileApplier_unknown_workflow_is_validation_error_without_orphan_configs()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var response = await server.Post(ScheduleRequest(request, ScheduleFile()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task ScheduleFileApplier_audit_failure_rolls_back_every_registration_write()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = (await Request(server)) with { Schedules = ScheduleFile(), ScheduleSourcePath = "schedules.json" };
        await Execute("""
            ALTER TABLE audit_log ADD CONSTRAINT reject_schedule_test
            CHECK (NOT (summary->'after'->'changes' ? 'scheduleConfigVersion')) NOT VALID
            """);
        try
        {
            using var response = await server.Post(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var failure = Assert.IsType<PostgresException>(Assert.Single(server.Failures));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal(0L, await Value("SELECT count(*) FROM workflows WHERE id=$1", request.WorkflowId!));
            Assert.Equal(0L, await Value("SELECT count(*) FROM teams WHERE id=$1", Team(request)));
            Assert.Equal(0L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(request)));
            Assert.Equal(0L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(0L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(0L, await Value("SELECT count(*) FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(0L, await Value("SELECT count(*) FROM run_series WHERE workflow_id=$1", request.WorkflowId!));
        }
        finally { await Execute("ALTER TABLE audit_log DROP CONSTRAINT reject_schedule_test"); }
    }

    [Fact]
    public async Task ScheduleFileApplier_concurrent_different_files_leave_one_whole_committed_snapshot()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var responses = await Task.WhenAll(Enumerable.Range(1, 12).Select(seconds =>
            server.Post(ScheduleRequest(request, $$"""
                {"instances":[
                    {"id":"stable","timezone":"Etc/UTC","interval":"{{TimeSpan.FromSeconds(seconds):c}}"},
                    {"id":"only-{{seconds}}","timezone":"Etc/UTC","interval":"00:01:00"}]}
                """))));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            Assert.Equal(12L, await Value("SELECT count(*) FROM schedule_config_versions WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Equal(2L, await Value("SELECT count(*) FROM schedules WHERE workflow_id=$1 AND deleted_at IS NULL", request.WorkflowId!));
            Assert.Equal(2L, await Value("""
                SELECT count(*) FROM workflows w JOIN schedules s
                    ON s.workflow_id=w.id AND s.config_version_id=w.active_schedule_config_id
                WHERE w.id=$1 AND s.deleted_at IS NULL
                """, request.WorkflowId!));
            Assert.Equal(1L, await Value("""
                SELECT count(*) FROM schedules s JOIN schedules unique_instance ON unique_instance.workflow_id=s.workflow_id
                WHERE s.workflow_id=$1 AND s.code_key='stable' AND unique_instance.code_key='only-'||s.interval_s
                    AND unique_instance.deleted_at IS NULL
                """, request.WorkflowId!));
            Assert.Equal(13L, await Value("SELECT count(*) FROM run_series WHERE workflow_id=$1", request.WorkflowId!));
            Assert.Empty(server.Failures);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ScheduleFileApplier_cron_timezone_policies_pin_and_defaults_are_persisted()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        request = request with { Manifest = ScheduleManifest(request.WorkflowId!, "1.0.0", 1) };
        using var registered = await server.Post(request);
        server.Clock.Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        using var applied = await server.Post(ScheduleRequest(request, """
            {"defaults":{"load":{"count":2}},"instances":[
              {"id":"stable","cron":"0 9 * * *","timezone":"America/New_York",
               "versionPin":"1.0.0","overlap":"Concurrent","catchUp":"None","variables":{"count":3}}]}
            """));
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        Assert.Equal(true, await Value("""
            SELECT cron='0 9 * * *' AND interval_s IS NULL AND timezone='America/New_York' AND version_pin='1.0.0'
                AND overlap_policy=2 AND catchup_policy=0 AND (variables->>'count')::int=3
            FROM schedules WHERE workflow_id=$1
            """, request.WorkflowId!));
        Assert.Equal(new DateTime(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc),
            await Value("SELECT next_fire_at FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task ScheduleFileApplier_legacy_matching_config_retains_content_without_rewriting_provenance()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var config = Guid.CreateVersion7();
        var content = ScheduleFile();
        await Execute("""
            INSERT INTO schedule_config_versions(id,workflow_id,source_path,git_commit,content_sha256,git_author,registered_by)
            VALUES($1,$2,'legacy.json',$3,$4,'Legacy author','legacy')
            """, config, request.WorkflowId!, request.GitCommit, SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        using var applied = await server.Post(ScheduleRequest(request, content));
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        Assert.Equal(config, (await Json(applied)).GetProperty("scheduleConfigVersion").GetGuid());
        Assert.Equal(content, await Value("SELECT content FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal("Legacy author", await Value("SELECT git_author FROM schedule_config_versions WHERE id=$1", config));
        Assert.Equal("legacy.json", await Value("SELECT source_path FROM schedule_config_versions WHERE id=$1", config));
    }

    [Fact]
    public async Task ScheduleFileApplier_database_guards_config_hash_and_workflow_and_series_identity()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        using var applied = await server.Post(ScheduleRequest(request, ScheduleFile()));
        var config = (await Json(applied)).GetProperty("scheduleConfigVersion").GetGuid();
        var id = Assert.IsType<Guid>(await Value("SELECT id FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        var hashError = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "UPDATE schedule_config_versions SET content='different' WHERE id=$1", config));
        Assert.Equal(PostgresErrorCodes.CheckViolation, hashError.SqlState);
        var second = await Request(server);
        using var secondRegistered = await server.Post(second);
        var linkError = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "UPDATE workflows SET active_schedule_config_id=$2 WHERE id=$1", second.WorkflowId!, config));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, linkError.SqlState);
        var seriesError = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "UPDATE run_series SET workflow_id=$2 WHERE schedule_id=$1", id, second.WorkflowId!));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, seriesError.SqlState);
    }

    private static RegistrationRequest ScheduleRequest(RegistrationRequest request, string content) =>
        new(request.GitCommit, request.GitAuthor, request.WorkflowId, Schedules: content,
            ScheduleSourcePath: "workflows/deploy.schedules.json");

    private static string ScheduleFile(int seconds = 60, string variables = "{}") => $$"""
        {"instances":[{"id":"stable","timezone":"Etc/UTC","interval":"{{TimeSpan.FromSeconds(seconds):c}}","variables":{{variables}}}]}
        """;

    private static JsonElement ScheduleManifest(string id, string version, int defaultValue)
    {
        var manifest = Manifest(id, version).Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        return JsonSerializer.SerializeToElement(manifest with
        {
            Variables = [new("count", VariableType.Int, @default: JsonSerializer.SerializeToElement(defaultValue))]
        }, WorkflowManifestJsonContext.Default.WorkflowManifest);
    }
}
