using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Scheduler;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class UserSchedulesTests(RegistrationDatabase database) : IClassFixture<RegistrationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string BasePath = "/api/v1/schedules";

    [Fact]
    public async Task UserSchedules_create_get_edit_pause_resume_delete_preserves_series_history_and_strong_ETags()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var created = await Create(server, prepared.Draft, bearer);
        Assert.Equal("User", created.Origin);
        Assert.False(created.Paused);
        Assert.Equal(1, created.RowVersion);
        Assert.Equal(2, created.Variables["count"].GetInt32());
        Assert.Equal("ui", created.Variables["env"].GetString());
        Assert.Equal(WholeMicroseconds(server.Clock.Now).AddMinutes(1), created.NextFireAt);
        Assert.Equal(prepared.Version, await Scalar("SELECT active_version_id FROM workflows WHERE id=$1", prepared.Request.WorkflowId!));
        Assert.Equal(DBNull.Value, await Scalar("SELECT config_version_id FROM schedules WHERE id=$1", created.Id));
        Assert.Equal(created.SeriesId, await Scalar("SELECT id FROM run_series WHERE schedule_id=$1", created.Id));
        using (var read = await Send(server, HttpMethod.Get, "/" + created.Id, bearer))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("\"1\"", read.Headers.ETag!.Tag);
            Assert.Equal("no-store", read.Headers.CacheControl!.ToString());
            Assert.Equal(created.Id, (await Read(read)).Id);
        }
        var originalCursor = created.NextFireAt;
        var edited = await Change(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"",
            prepared.Draft with { Name = "edited", Variables = Values("""{"env":"changed","count":7}""") });
        Assert.Equal(2, edited.RowVersion);
        Assert.Equal(originalCursor, edited.NextFireAt);
        Assert.Equal(created.SeriesId, edited.SeriesId);
        Assert.Equal("changed", edited.Variables["env"].GetString());
        var paused = await Change(server, HttpMethod.Post, "/" + created.Id + "/pause", bearer, "\"2\"");
        Assert.True(paused.Paused);
        Assert.Null(paused.NextFireAt);
        Assert.Equal(3, paused.RowVersion);
        var again = await Change(server, HttpMethod.Post, "/" + created.Id + "/pause", bearer, "\"3\"");
        Assert.Equal(3, again.RowVersion);
        server.Clock.Now = server.Clock.Now.AddHours(1);
        var resumed = await Change(server, HttpMethod.Post, "/" + created.Id + "/resume", bearer, "\"3\"");
        Assert.False(resumed.Paused);
        Assert.Equal(WholeMicroseconds(server.Clock.Now).AddMinutes(1), resumed.NextFireAt);
        Assert.Equal(4, resumed.RowVersion);
        using var deleted = await Send(server, HttpMethod.Delete, "/" + created.Id, bearer, "\"4\"");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("\"5\"", deleted.Headers.ETag!.Tag);
        using var missing = await Send(server, HttpMethod.Get, "/" + created.Id, bearer);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(true, await Scalar("SELECT deleted_at IS NOT NULL AND paused AND next_fire_at IS NULL FROM schedules WHERE id=$1", created.Id));
        Assert.Equal(created.SeriesId, await Scalar("SELECT id FROM run_series WHERE schedule_id=$1", created.Id));
        Assert.Equal(5L, await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1", created.Id.ToString("D")));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", created.SeriesId));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("Operator", "app", "wf.read", "60d24b19-f65a-4ed4-96cc-9174bfcacabc", true, 403)]
    [InlineData("Admin", "app", null, "60d24b19-f65a-4ed4-96cc-9174bfcacabc", true, 403)]
    [InlineData("Registrar", "app", null, "60d24b19-f65a-4ed4-96cc-9174bfcacabc", true, 403)]
    [InlineData("Operator", "user", null, "60d24b19-f65a-4ed4-96cc-9174bfcacabc", true, 403)]
    [InlineData("Operator", "user", "wf.read", null, true, 403)]
    [InlineData("Admin", "user", "wf.read", "60d24b19-f65a-4ed4-96cc-9174bfcacab0", true, 403)]
    [InlineData("Operator", "user", "wf.read", "60d24b19-f65a-4ed4-96cc-9174bfcacabc", false, 403)]
    [InlineData("Operator", "user", "wf.read", "60d24b19-f65a-4ed4-96cc-9174bfcacabc", true, 201)]
    public async Task UserSchedules_actual_signed_token_requires_delegated_SPA_identity_even_for_global_Admin(
        string role, string identity, string? scope, string? client, bool actor, int expected)
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var token = server.Token(role, identity, scope, actor, [prepared.Group], authorizedClient: client);
        using var response = await Send(server, HttpMethod.Post, "", token, draft: prepared.Draft);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected == 201 ? 1L : 0L,
            await Scalar("SELECT count(*) FROM schedules WHERE workflow_id=$1", prepared.Request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData(RegistryRole.Author, "Author", 403)]
    [InlineData(RegistryRole.Viewer, "Viewer", 403)]
    [InlineData(RegistryRole.Operator, "Operator", 201)]
    [InlineData(RegistryRole.Admin, "Operator", 201)]
    [InlineData(RegistryRole.Viewer, "Admin", 201)]
    public async Task UserSchedules_scoped_role_matrix_keeps_Author_and_Operator_independent(
        RegistryRole binding, string role, int expected)
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server, binding);
        using var response = await Send(server, HttpMethod.Post, "",
            server.Token(role, "user", "wf.read", groups: [prepared.Group]), draft: prepared.Draft);
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 403) Assert.Equal("insufficient_scope_role", (await Json(response)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task UserSchedules_public_reads_do_not_require_SPA_but_mutations_check_scope_and_revocation_on_every_request()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var created = await Create(server, prepared.Draft, bearer);
        using (var read = await Send(server, HttpMethod.Get, "/" + created.Id, server.Token("Admin", authorizedClient: null)))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var denied = await Send(server, HttpMethod.Put, "/" + created.Id, Operator(server, Guid.NewGuid()), "\"1\"", prepared.Draft))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var noIdentity = await Send(server, HttpMethod.Post, "", server.Token("Operator", "user", "wf.read",
            groups: [prepared.Group], includeIdentity: false), draft: prepared.Draft with { Name = "delegated-v2" }))
            Assert.Equal(HttpStatusCode.Created, noIdentity.StatusCode);
        using (var anonymous = await server.Send(HttpMethod.Post, BasePath, JsonContent.Create(prepared.Draft,
            UserScheduleJson.Default.UserScheduleRequest), useDefaultToken: false))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var registry = prepared.Request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)! with { RoleBindings = [] };
        using var revoked = await server.Post(new(prepared.Request.GitCommit, prepared.Request.GitAuthor,
            Registry: JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry)));
        Assert.Equal(HttpStatusCode.Created, revoked.StatusCode);
        using var forbiddenRead = await Send(server, HttpMethod.Get, "/" + created.Id, bearer);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        using var forbiddenEdit = await Send(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"", prepared.Draft);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenEdit.StatusCode);
        Assert.Equal(1, await Scalar("SELECT row_version FROM schedules WHERE id=$1", created.Id));
    }

    [Theory]
    [InlineData("put")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("delete")]
    public async Task CodeScheduleReadOnly_every_mutation_conflicts_without_changing_configuration_or_series(string mutation)
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        using var registered = await server.Post(prepared.Request with
        {
            Schedules = """{"instances":[{"id":"code","timezone":"Etc/UTC","interval":"00:01:00"}]}""",
            ScheduleSourcePath = "workflow.schedules.json"
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = Assert.IsType<Guid>(await Scalar("SELECT id FROM schedules WHERE workflow_id=$1", prepared.Request.WorkflowId!));
        var series = await Scalar("SELECT id FROM run_series WHERE schedule_id=$1", id);
        var xmin = await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", id);
        using var read = await Send(server, HttpMethod.Get, "/" + id, Operator(server, prepared.Group));
        var code = await Read(read);
        Assert.Equal("Code", code.Origin);
        Assert.Equal("workflow.schedules.json", code.SourcePath);
        Assert.Equal(prepared.Request.GitCommit, code.GitCommit);
        var method = mutation == "put" ? HttpMethod.Put : mutation == "delete" ? HttpMethod.Delete : HttpMethod.Post;
        var suffix = "/" + id + (mutation is "pause" or "resume" ? "/" + mutation : "");
        using var denied = await Send(server, method, suffix, Operator(server, prepared.Group),
            draft: mutation == "put" ? prepared.Draft : null);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(xmin, await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", id));
        Assert.Equal(series, await Scalar("SELECT id FROM run_series WHERE schedule_id=$1", id));
    }

    [Fact]
    public async Task UserSchedules_missing_stale_and_ambiguous_If_Match_never_mutate_the_current_configuration()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var created = await Create(server, prepared.Draft, bearer);
        foreach (var mutation in new[] { "put", "pause", "resume", "delete" })
        {
            var method = mutation == "put" ? HttpMethod.Put : mutation == "delete" ? HttpMethod.Delete : HttpMethod.Post;
            var suffix = "/" + created.Id + (mutation is "pause" or "resume" ? "/" + mutation : "");
            var draft = mutation == "put" ? prepared.Draft : null;
            using var missing = await Send(server, method, suffix, bearer, draft: draft);
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
            using var stale = await Send(server, method, suffix, bearer, "\"999\"", draft);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            foreach (var invalid in new[] { "*", "W/\"1\"", "\"01\"", "\"1\",\"2\"", "\"0\"", "\"2147483648\"" })
            {
                using var bad = await Send(server, method, suffix, bearer, invalid, draft);
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            }
        }
        using var multiple = await server.Send(HttpMethod.Put, BasePath + "/" + created.Id,
            JsonContent.Create(prepared.Draft, UserScheduleJson.Default.UserScheduleRequest), bearer,
            ifMatch: ["\"1\"", "\"2\""]);
        Assert.Equal(HttpStatusCode.BadRequest, multiple.StatusCode);
        Assert.Equal(1, await Scalar("SELECT row_version FROM schedules WHERE id=$1", created.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1", created.Id.ToString("D")));
    }

    [Fact]
    public async Task UserSchedules_sixteen_concurrent_edits_allow_one_winner_and_reject_every_lost_update()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var created = await Create(server, prepared.Draft, bearer);
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            Send(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"", prepared.Draft with { Name = "winner-" + index })));
        try
        {
            var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(15, responses.Count(response => response.StatusCode == HttpStatusCode.PreconditionFailed));
            Assert.Equal("\"2\"", winner.Headers.ETag!.Tag);
            var current = await Read(winner);
            Assert.Equal(current.Name, await Scalar("SELECT name FROM schedules WHERE id=$1", created.Id));
            Assert.Equal(2, await Scalar("SELECT row_version FROM schedules WHERE id=$1", created.Id));
            Assert.Equal(created.NextFireAt, current.NextFireAt);
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1 AND action='schedule.edit'", created.Id.ToString("D")));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task UserSchedules_semantic_noop_preserves_ETag_xmin_cursor_and_audit_and_cannot_move_a_workflow()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var created = await Create(server, prepared.Draft, bearer);
        var xmin = await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", created.Id);
        var same = await Change(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"", prepared.Draft);
        Assert.Equal(1, same.RowVersion);
        Assert.Equal(created.NextFireAt, same.NextFireAt);
        Assert.Equal(xmin, await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", created.Id));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1", created.Id.ToString("D")));
        using var moved = await Send(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"",
            prepared.Draft with { WorkflowId = "other/release/workflow" });
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Equal("$.workflowId", (await Json(moved)).GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal(prepared.Request.WorkflowId, await Scalar("SELECT workflow_id FROM schedules WHERE id=$1", created.Id));
        var changed = await Change(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"",
            prepared.Draft with { Interval = TimeSpan.FromMinutes(2) });
        Assert.Equal(WholeMicroseconds(server.Clock.Now).AddMinutes(2), changed.NextFireAt);
        Assert.Equal(created.SeriesId, changed.SeriesId);
    }

    [Fact]
    public async Task UserSchedules_preview_uses_shared_timezone_DST_semantics_and_does_not_write_rows()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        server.Clock.Now = new(2026, 11, 1, 5, 29, 0, TimeSpan.Zero);
        var draft = prepared.Draft with { Cron = "30 1 * * *", Interval = null, Timezone = "America/New_York" };
        using var preview = await Send(server, HttpMethod.Post, "/preview", Operator(server, prepared.Group), draft: draft);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var fires = (await preview.Content.ReadFromJsonAsync(UserScheduleJson.Default.UserSchedulePreview, Token))!.Fires;
        Assert.Equal(5, fires.Length);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), fires[0]);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), fires[1]);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedules WHERE workflow_id=$1", prepared.Request.WorkflowId!));
        var saved = await Create(server, draft, Operator(server, prepared.Group));
        Assert.Equal(fires[0], saved.NextFireAt);
        Assert.Equal(draft.Timezone, saved.Timezone);
        Assert.Equal(draft.Cron, saved.Cron);
    }

    [Fact]
    public async Task UserSchedules_pinned_versions_validate_their_own_variable_schema_not_the_active_versions_schema()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var original = prepared.Request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        using var registered = await server.Post(prepared.Request with
        {
            Manifest = JsonSerializer.SerializeToElement(original with
            {
                Version = "2.0.0", Variables = original.Variables.Add(new("new-required", VariableType.String, true))
            }, WorkflowManifestJsonContext.Default.WorkflowManifest)
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var bearer = Operator(server, prepared.Group);
        var pinned = await Create(server, prepared.Draft with { VersionPin = prepared.Version.ToString("D") }, bearer);
        Assert.Equal(prepared.Version.ToString("D"), pinned.VersionPin);
        using var activeInvalid = await Send(server, HttpMethod.Post, "", bearer, draft: prepared.Draft);
        Assert.Equal(HttpStatusCode.BadRequest, activeInvalid.StatusCode);
        Assert.Contains("new-required", (await Json(activeInvalid)).GetProperty("errors")[0].GetProperty("path").GetString());
        var ranged = await Create(server, prepared.Draft with { VersionPin = "1.*" }, bearer);
        Assert.Equal("1.*", ranged.VersionPin);
        foreach (var pin in new[] { "^1.0.0", Guid.Empty.ToString("D") })
        {
            using var invalid = await Send(server, HttpMethod.Post, "", bearer, draft: prepared.Draft with { VersionPin = pin });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("$.versionPin", (await Json(invalid)).GetProperty("errors")[0].GetProperty("path").GetString());
        }
        using var absent = await Send(server, HttpMethod.Post, "", bearer, draft: prepared.Draft with { VersionPin = "9.*" });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    [Theory]
    [InlineData("both-expressions")]
    [InlineData("no-expression")]
    [InlineData("fractional-interval")]
    [InlineData("huge-interval")]
    [InlineData("zero-interval")]
    [InlineData("bad-cron")]
    [InlineData("windows-timezone")]
    [InlineData("unknown-variable")]
    [InlineData("bad-variable")]
    [InlineData("nul-value")]
    [InlineData("empty-name")]
    [InlineData("control-name")]
    [InlineData("long-name")]
    public async Task UserSchedules_invalid_drafts_fail_with_sanitized_shared_errors_before_any_mutation(string invalid)
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var draft = invalid switch
        {
            "both-expressions" => prepared.Draft with { Cron = "* * * * *" },
            "no-expression" => prepared.Draft with { Interval = null },
            "fractional-interval" => prepared.Draft with { Interval = TimeSpan.FromMilliseconds(500) },
            "huge-interval" => prepared.Draft with { Interval = TimeSpan.FromSeconds((long)int.MaxValue + 1) },
            "zero-interval" => prepared.Draft with { Interval = TimeSpan.Zero },
            "bad-cron" => prepared.Draft with { Interval = null, Cron = "not-a-cron" },
            "windows-timezone" => prepared.Draft with { Timezone = "Pacific Standard Time" },
            "unknown-variable" => prepared.Draft with { Variables = Values("""{"unknown":"never-echo-this-value"}""") },
            "bad-variable" => prepared.Draft with { Variables = Values("""{"count":"never-echo-this-value"}""") },
            "nul-value" => prepared.Draft with { Variables = Values("""{"env":"never-echo-this-value\u0000"}""") },
            "empty-name" => prepared.Draft with { Name = " " },
            "control-name" => prepared.Draft with { Name = "bad\nname" },
            "long-name" => prepared.Draft with { Name = new('n', 129) },
            _ => throw new InvalidOperationException()
        };
        using var response = await Send(server, HttpMethod.Post, "", Operator(server, prepared.Group), draft: draft);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("never-echo-this-value", body);
        Assert.NotEmpty((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedules WHERE workflow_id=$1", prepared.Request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("\"not-an-object\"")]
    public async Task UserSchedules_bad_JSON_shapes_are_problem_details_not_unhandled_failures(string json)
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        using var response = await server.Send(HttpMethod.Post, BasePath, new StringContent(json, Encoding.UTF8, "application/json"),
            Operator(server, prepared.Group));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task UserSchedules_exact_body_limit_streamed_limit_null_variables_and_empty_action_bodies_are_enforced()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var raw = JsonSerializer.Serialize(prepared.Draft, UserScheduleJson.Default.UserScheduleRequest);
        var size = Encoding.UTF8.GetByteCount(raw);
        using var exact = await server.Send(HttpMethod.Post, BasePath,
            new StringContent(raw + new string(' ', 1024 * 1024 - size), Encoding.UTF8, "application/json"), bearer);
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        var schedule = await Read(exact);
        using var over = await server.Send(HttpMethod.Post, BasePath,
            new StringContent(raw + new string(' ', 1024 * 1024 - size + 1), Encoding.UTF8, "application/json"), bearer);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        using (var content = new StreamContent(new NonSeekingStream(Encoding.UTF8.GetBytes(raw + new string(' ', 1024 * 1024 - size + 1)))))
        {
            content.Headers.ContentType = new("application/json");
            using var streamed = await server.Send(HttpMethod.Post, BasePath, content, bearer);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        }
        using var media = await server.Send(HttpMethod.Post, BasePath, new StringContent(raw), bearer);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        var node = JsonSerializer.SerializeToNode(prepared.Draft, UserScheduleJson.Default.UserScheduleRequest)!;
        node["variables"] = null;
        using var nullVariables = await server.Send(HttpMethod.Post, BasePath,
            new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"), bearer);
        Assert.Equal(HttpStatusCode.BadRequest, nullVariables.StatusCode);
        foreach (var body in new[] { "null", "[]", """{"paused":true}""" })
        {
            using var badAction = await server.Send(HttpMethod.Post, BasePath + "/" + schedule.Id + "/pause",
                new StringContent(body, Encoding.UTF8, "application/json"), bearer, ifMatch: ["\"1\""]);
            Assert.Equal(HttpStatusCode.BadRequest, badAction.StatusCode);
        }
        var unicode = await Create(server, prepared.Draft with { Name = string.Concat(Enumerable.Repeat("\U0001f600", 128)) }, bearer);
        Assert.Equal(256, unicode.Name.Length);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task UserSchedules_audit_failure_rolls_back_create_and_edit_with_no_orphan_series_or_notification()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var name = "reject_ui_schedule_" + Guid.NewGuid().ToString("N");
        var team = prepared.Request.WorkflowId!.Split('/')[0];
        await Execute($$"""
            CREATE FUNCTION {{name}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.target_kind='Schedule' AND NEW.team_id='{{team}}' THEN RAISE EXCEPTION 'injected schedule audit failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {{name}} BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION {{name}}()
            """);
        try
        {
            using var failed = await Send(server, HttpMethod.Post, "", bearer, draft: prepared.Draft);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedules WHERE workflow_id=$1", prepared.Request.WorkflowId!));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM run_series WHERE workflow_id=$1", prepared.Request.WorkflowId!));
        }
        finally { await Execute($"DROP TRIGGER {name} ON audit_log; DROP FUNCTION {name}()"); }
        var created = await Create(server, prepared.Draft, bearer);
        await Execute($$"""
            CREATE FUNCTION {{name}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.target_kind='Schedule' AND NEW.team_id='{{team}}' THEN RAISE EXCEPTION 'injected schedule audit failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {{name}} BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION {{name}}()
            """);
        var notifications = 0;
        await using var listener = await database.DataSource.OpenConnectionAsync(Token);
        listener.Notification += (_, notification) => { if (notification.Channel == "schedules") notifications++; };
        await using (var listen = new NpgsqlCommand("LISTEN schedules", listener)) await listen.ExecuteNonQueryAsync(Token);
        try
        {
            using var failed = await Send(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"", prepared.Draft with { Name = "never-committed" });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(created.Name, await Scalar("SELECT name FROM schedules WHERE id=$1", created.Id));
            Assert.Equal(1, await Scalar("SELECT row_version FROM schedules WHERE id=$1", created.Id));
            Assert.False(await listener.WaitAsync(100, Token));
            Assert.Equal(0, notifications);
        }
        finally { await Execute($"DROP TRIGGER {name} ON audit_log; DROP FUNCTION {name}()"); }
        Assert.Equal(2, server.Failures.Count);
        Assert.All(server.Failures, error => Assert.IsType<PostgresException>(error));
    }

    [Fact]
    public async Task UserSchedules_UI_edits_reload_the_hosted_scheduler_and_resume_does_not_catch_up_an_intentional_pause()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var bearer = Operator(server, prepared.Group);
        var created = await Create(server, prepared.Draft with { Overlap = Wf.Contracts.Schedules.ScheduleOverlapPolicy.Concurrent }, bearer);
        using var lease = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5400")),
            NullLogger<LeaseManager>.Instance);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, lease);
        using var recovery = new RunRecovery(database.DataSource, store, lease, server.Clock, NullLogger<RunRecovery>.Instance);
        using var scheduler = new ScheduleFireService(database.DataSource, lease, recovery, server.Versions, new(server.Clock),
            server.Clock, NullLogger<ScheduleFireService>.Instance);
        await lease.StartAsync(Token);
        await recovery.StartAsync(Token);
        await scheduler.StartAsync(Token);
        try
        {
            await WaitFor(() => scheduler.IsReady);
            var edited = await Change(server, HttpMethod.Put, "/" + created.Id, bearer, "\"1\"", prepared.Draft with
            {
                Overlap = Wf.Contracts.Schedules.ScheduleOverlapPolicy.Concurrent, Variables = Values("""{"env":"live-edit","count":9}""")
            });
            Assert.Equal(created.NextFireAt, edited.NextFireAt);
            server.Clock.Now = edited.NextFireAt!.Value;
            await WaitFor(async () => (long)(await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", created.SeriesId))! == 1);
            var run = Assert.IsType<Guid>(await Scalar("SELECT id FROM runs WHERE series_id=$1", created.SeriesId));
            Assert.Equal(9, (await store.LoadAsync(run, Token)).Snapshot.Variables["count"].GetInt32());
            var paused = await Change(server, HttpMethod.Post, "/" + created.Id + "/pause", bearer, "\"2\"");
            server.Clock.Now = server.Clock.Now.AddMinutes(10);
            var resumed = await Change(server, HttpMethod.Post, "/" + created.Id + "/resume", bearer, ScheduleEntityTag.Format(paused.RowVersion));
            Assert.Equal(server.Clock.Now.AddMinutes(1), resumed.NextFireAt);
            server.Clock.Now = resumed.NextFireAt!.Value;
            await WaitFor(async () => (long)(await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", created.SeriesId))! == 2);
            Assert.Equal(2L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=0", created.Id));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 AND outcome=2", created.Id));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1 AND trigger=2", created.SeriesId));
        }
        finally
        {
            await scheduler.StopAsync(Token);
            await recovery.StopAsync(Token);
            await lease.StopAsync(Token);
        }
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task UserSchedules_actual_public_and_internal_OpenAPI_documents_separate_schedule_mutations()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        using var publicResponse = await server.Send(HttpMethod.Get, "/openapi/v1.json", new ByteArrayContent([]), server.Token("Admin"));
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        var publicDocument = await Json(publicResponse);
        Assert.StartsWith("3.1", publicDocument.GetProperty("openapi").GetString());
        var publicPaths = publicDocument.GetProperty("paths");
        Assert.True(publicPaths.GetProperty(BasePath).TryGetProperty("get", out _));
        Assert.False(publicPaths.GetProperty(BasePath).TryGetProperty("post", out _));
        Assert.True(publicPaths.GetProperty("/api/v1/schedules/{id}").TryGetProperty("get", out _));
        Assert.False(publicPaths.GetProperty("/api/v1/schedules/{id}").TryGetProperty("put", out _));
        Assert.False(publicPaths.GetProperty("/api/v1/schedules/{id}").TryGetProperty("delete", out _));
        Assert.True(publicPaths.GetProperty("/api/v1/schedules/{id}/run-now").TryGetProperty("post", out _));
        foreach (var path in new[] { BasePath + "/preview", BasePath + "/{id}/pause", BasePath + "/{id}/resume" })
            Assert.False(publicPaths.TryGetProperty(path, out _), $"UI mutation leaked into public OpenAPI: {path}");
        using var internalResponse = await server.Send(HttpMethod.Get, "/openapi/ui.json", new ByteArrayContent([]), server.Token("Admin"));
        Assert.Equal(HttpStatusCode.OK, internalResponse.StatusCode);
        var internalDocument = await Json(internalResponse);
        var internalPaths = internalDocument.GetProperty("paths");
        Assert.True(internalPaths.GetProperty(BasePath).TryGetProperty("post", out _));
        Assert.True(internalPaths.GetProperty(BasePath + "/{id}").TryGetProperty("put", out _));
        Assert.True(internalPaths.GetProperty(BasePath + "/{id}").TryGetProperty("delete", out _));
        Assert.True(internalPaths.GetProperty(BasePath + "/{id}/pause").TryGetProperty("post", out _));
        Assert.True(internalPaths.GetProperty(BasePath + "/{id}/resume").TryGetProperty("post", out _));
        Assert.True(internalPaths.GetProperty(BasePath + "/preview").TryGetProperty("post", out _));
        Assert.True(internalPaths.GetProperty(BasePath).GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").TryGetProperty("application/json", out _));
        Assert.False(internalPaths.TryGetProperty("/api/v1/runs", out _));
        using var anonymous = await server.Send(HttpMethod.Get, "/openapi/v1.json", new ByteArrayContent([]), useDefaultToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(server.Failures);
    }

    private async Task<Prepared> Prepare(Server server, RegistryRole binding = RegistryRole.Operator)
    {
        var request = await Request(server);
        var group = Guid.NewGuid();
        var registry = request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)! with
            { RoleBindings = [new(binding, group)] };
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [new("env", VariableType.String, true, JsonSerializer.SerializeToElement("fallback")),
                new("count", VariableType.Int, true, JsonSerializer.SerializeToElement(2))]
        };
        request = request with
        {
            Registry = JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry),
            Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest)
        };
        using var registered = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => server.Versions.IsReady && HasVersion());
        return new(request, group, version, new(request.WorkflowId!, "UI nightly", "Etc/UTC", Interval: TimeSpan.FromMinutes(1))
        {
            Variables = Values("""{"env":"ui"}""")
        });

        bool HasVersion()
        {
            try { return server.Versions.GetActive(request.WorkflowId!).VersionId == version; }
            catch (KeyNotFoundException) { return false; }
        }
    }

    private static string Operator(Server server, Guid group) => server.Token("Operator", "user", "wf.read", groups: [group]);
    private static ImmutableDictionary<string, JsonElement> Values(string json) =>
        JsonSerializer.Deserialize<ImmutableDictionary<string, JsonElement>>(json)!;
    private static DateTimeOffset WholeMicroseconds(DateTimeOffset time) => new(time.UtcTicks / 10 * 10, TimeSpan.Zero);

    private static async Task<UserScheduleResponse> Create(Server server, UserScheduleRequest draft, string token)
    {
        using var response = await Send(server, HttpMethod.Post, "", token, draft: draft);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
        var schedule = await Read(response);
        Assert.EndsWith(schedule.Id.ToString("D"), response.Headers.Location!.ToString());
        return schedule;
    }

    private static async Task<UserScheduleResponse> Change(Server server, HttpMethod method, string suffix, string token,
        string etag, UserScheduleRequest? draft = null)
    {
        using var response = await Send(server, method, suffix, token, etag, draft);
        Assert.Equal(method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK, response.StatusCode);
        var schedule = await Read(response);
        Assert.Equal(ScheduleEntityTag.Format(schedule.RowVersion), response.Headers.ETag!.Tag);
        return schedule;
    }

    private static Task<HttpResponseMessage> Send(Server server, HttpMethod method, string suffix, string token,
        string? etag = null, UserScheduleRequest? draft = null) =>
        server.Send(method, BasePath + suffix,
            draft is null ? new ByteArrayContent([]) : JsonContent.Create(draft, UserScheduleJson.Default.UserScheduleRequest),
            token, ifMatch: etag is null ? null : [etag]);

    private static async Task<UserScheduleResponse> Read(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(UserScheduleJson.Default.UserScheduleResponse, Token))!;

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

    private sealed record Prepared(RegistrationRequest Request, Guid Group, Guid Version, UserScheduleRequest Draft);
    private sealed class NonSeekingStream(byte[] data) : MemoryStream(data, writable: false) { public override bool CanSeek => false; }
}
