using System.Net;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Wf.Contracts.Runs;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    public async Task SignOff_actual_OpenAPI_declares_path_preconditions_and_string_enums()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        using var response = await server.Send(HttpMethod.Get, "/openapi/v1.json", null!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await Json(response);
        foreach (var report in new[] { false, true })
        foreach (var suffix in new[] { "", "/revoke" })
        {
            var path = "/api/v1/runs/{id}" + (report ? "/reports/{reportId}" : "") + "/signoff" + suffix;
            var operation = document.GetProperty("paths").GetProperty(path).GetProperty("post");
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.Equal(report ? ["id", "reportId"] : new[] { "id" },
                parameters.Where(item => item.GetProperty("in").GetString() == "path")
                    .Select(item => item.GetProperty("name").GetString()).Order());
            Assert.True(Assert.Single(parameters,
                item => item.GetProperty("name").GetString() == "If-Match").GetProperty("required").GetBoolean());
        }
        var schemas = document.GetProperty("components").GetProperty("schemas");
        Assert.Equal(["NotSignedOff", "SignedOff", "Revoked"],
            schemas.GetProperty("SignOffStatus").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["SignOff", "Revoke"],
            schemas.GetProperty("SignOffAction").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(server.Failures);
    }

    private static string SignOffPath(Guid run, Guid? report = null) =>
        $"/api/v1/runs/{run:D}" + (report is { } id ? $"/reports/{id:D}" : "") + "/signoff";

    private static async Task<SignOffResponse> SignOffRead(Server server, string path, string bearer)
    {
        using var response = await server.Send(HttpMethod.Get, path, null!, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var value = (await response.Content.ReadFromJsonAsync(SignOffJsonContext.Default.SignOffResponse,
            TestContext.Current.CancellationToken))!;
        Assert.Equal($"\"{value.State.Revision}\"", response.Headers.ETag!.Tag);
        return value;
    }

    private static Task<HttpResponseMessage> SignOffWrite(Server server, string path, string bearer,
        string revision = "0", string reason = "Reviewed all results", bool revoke = false) =>
        server.Send(HttpMethod.Post, path + (revoke ? "/revoke" : ""),
            JsonContent.Create(new SignOffRequest(reason), SignOffJsonContext.Default.SignOffRequest),
            bearer, ifMatch: [$"\"{revision}\""]);

    [Fact]
    public async Task SignOff_actual_HTTP_sign_revoke_resign_retains_pinned_run_actor_reason_and_all_viewer_history()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var viewerGroup = Guid.NewGuid();
        var seed = await SeedRerun(server, viewerGroup: viewerGroup);
        var writer = server.Token("", "user", "api.read", groups: [seed.Group], name: "Review Owner");
        var viewer = server.Token("", "user", "api.read", groups: [viewerGroup]);
        var path = SignOffPath(seed.Snapshot.RunId);
        var empty = await SignOffRead(server, path, viewer);
        Assert.Equal(new SignOffState("0", SignOffStatus.NotSignedOff, null), empty.State);
        Assert.Empty(empty.History);
        Assert.True(empty.IsTerminal);
        Assert.Equal(seed.Request.WorkflowId, empty.WorkflowId);
        Assert.Equal(seed.Version, empty.VersionId);
        Assert.Null(empty.ReportId);
        using (var signed = await SignOffWrite(server, path, writer, reason: "  Checked <script> and results  "))
            Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var first = await SignOffRead(server, path, viewer);
        Assert.Equal(SignOffStatus.SignedOff, first.State.Status);
        var approval = Assert.Single(first.History);
        Assert.Equal("Checked <script> and results", approval.Reason);
        Assert.Equal(Server.Actor, approval.ActorId);
        Assert.Equal("Review Owner", approval.ActorName);
        Assert.Equal(TimeSpan.Zero, approval.At.Offset);
        Assert.InRange((approval.At - server.Clock.GetUtcNow()).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        var otherActor = Guid.NewGuid();
        var revoker = server.Token("", "user", "api.read", groups: [seed.Group], actor: otherActor, name: "Second reviewer");
        using (var revoked = await SignOffWrite(server, path, revoker, "1", "Regression discovered", revoke: true))
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var second = await SignOffRead(server, path, viewer);
        Assert.Equal(SignOffStatus.Revoked, second.State.Status);
        Assert.Equal(otherActor, second.State.Latest!.ActorId);
        Assert.Equal(SignOffAction.Revoke, second.History[0].Action);
        Assert.Equal(approval, second.History[1]);
        using (var again = await SignOffWrite(server, path, writer, "2", "Regression verified fixed"))
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var page = await SignOffRead(server, path + "?limit=1", viewer);
        Assert.Equal("3", page.State.Revision);
        Assert.Equal("3", Assert.Single(page.History).Revision);
        Assert.Equal("3", page.NextBefore);
        page = await SignOffRead(server, path + "?limit=1&before=3", viewer);
        Assert.Equal("3", page.State.Revision);
        Assert.Equal("2", Assert.Single(page.History).Revision);
        Assert.Equal("2", page.NextBefore);
        page = await SignOffRead(server, path + "?limit=1&before=2", viewer);
        Assert.Equal("1", Assert.Single(page.History).Revision);
        Assert.Null(page.NextBefore);
        Assert.Equal(3L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1 AND action LIKE 'run.signoff%'", seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task SignOff_actual_HTTP_report_and_run_approvals_are_independent_and_cannot_cross_runs()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var bearer = RerunOperator(server, seed.Group);
        var report = Guid.CreateVersion7();
        await Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,'build/compile',1,6,now()+interval '1 hour')
            """, seed.Snapshot.RunId);
        await Execute("INSERT INTO report_ids(id,run_day) VALUES($1,uuid_v7_run_day($2))", report, seed.Snapshot.RunId);
        await Execute("""
            INSERT INTO reports(run_day,id,run_id,source_key,attempt,kind,title,tags,size_bytes,blob_uri,sha256,created_at)
            SELECT run_day,$2,run_id,task_key,attempt,'text','Review report',ARRAY[]::text[],0,
                'reports/'||run_id::text||'/'||$2::text,decode(repeat('00',32),'hex'),now()
            FROM task_attempts WHERE run_id=$1 ORDER BY task_key,attempt LIMIT 1
            """, seed.Snapshot.RunId, report);
        Assert.Equal(1L, await Value("SELECT count(*) FROM reports WHERE id=$1", report));
        var reportPath = SignOffPath(seed.Snapshot.RunId, report);
        using (var response = await SignOffWrite(server, reportPath, bearer))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signed = await SignOffRead(server, reportPath, bearer);
        Assert.Equal(report, signed.ReportId);
        Assert.Equal(SignOffStatus.SignedOff, signed.State.Status);
        Assert.Equal(SignOffStatus.NotSignedOff, (await SignOffRead(server, SignOffPath(seed.Snapshot.RunId), bearer)).State.Status);
        using (var response = await SignOffWrite(server, SignOffPath(seed.Snapshot.RunId), bearer))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await SignOffWrite(server, reportPath, bearer, "1", revoke: true))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SignOffStatus.SignedOff, (await SignOffRead(server, SignOffPath(seed.Snapshot.RunId), bearer)).State.Status);
        Assert.Equal(SignOffStatus.Revoked, (await SignOffRead(server, reportPath, bearer)).State.Status);
        var other = await SeedRerun(server);
        var otherBearer = RerunOperator(server, other.Group);
        using (var response = await SignOffWrite(server, SignOffPath(other.Snapshot.RunId, report), otherBearer))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(SignOffStatus.NotSignedOff, (await SignOffRead(server, SignOffPath(other.Snapshot.RunId), otherBearer)).State.Status);
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("", 428)]
    [InlineData("*", 400)]
    [InlineData("W/\"0\"", 400)]
    [InlineData("\"00\"", 400)]
    [InlineData("\"-1\"", 400)]
    [InlineData("\"0\", \"0\"", 400)]
    [InlineData("\"1\"", 412)]
    public async Task SignOff_actual_HTTP_requires_current_strong_ETag(string match, int status)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        using var response = await server.Send(HttpMethod.Post, SignOffPath(seed.Snapshot.RunId),
            new StringContent("""{"reason":"reviewed"}""", Encoding.UTF8, "application/json"),
            RerunOperator(server, seed.Group), ifMatch: match.Length == 0 ? null : [match]);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0L, await Value("SELECT count(*) FROM run_signoff_events WHERE run_id=$1", seed.Snapshot.RunId));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"reason":null}""")]
    [InlineData("""{"reason":""}""")]
    [InlineData("""{"reason":" \t\r\n"}""")]
    [InlineData("""{"reason":"bad\u0000reason"}""")]
    [InlineData("""{"reason":"checked","actorName":"spoof"}""")]
    [InlineData("""{"reason":"one","reason":"two"}""")]
    public async Task SignOff_actual_HTTP_rejects_invalid_reasons_and_spoofed_identity(string body)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        using var response = await server.Send(HttpMethod.Post, SignOffPath(seed.Snapshot.RunId),
            new StringContent(body, Encoding.UTF8, "application/json"), RerunOperator(server, seed.Group), ifMatch: ["\"0\""]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await Value("SELECT count(*) FROM run_signoff_events WHERE run_id=$1", seed.Snapshot.RunId));
    }

    [Fact]
    public async Task SignOff_actual_HTTP_checks_fresh_roles_terminal_state_content_type_size_and_query_boundaries()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var viewerGroup = Guid.NewGuid();
        var seed = await SeedRerun(server, viewerGroup: viewerGroup);
        var path = SignOffPath(seed.Snapshot.RunId);
        var writer = RerunOperator(server, seed.Group);
        var viewer = server.Token("", "user", "api.read", groups: [viewerGroup]);
        using (var denied = await SignOffWrite(server, path, viewer))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await SignOffWrite(server, path, server.Token("Registrar")))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var unknown = await server.Send(HttpMethod.Get, SignOffPath(Guid.CreateVersion7()), null!, writer))
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        using (var denied = await server.Send(HttpMethod.Get, path, null!, useDefaultToken: false))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        foreach (var query in new[] { "?before=0", "?before=01", "?limit=51", "?limit=0", "?limit=1&limit=1", "?Limit=1", "?unknown=x" })
        {
            using var invalid = await server.Send(HttpMethod.Get, path + query, null!, viewer);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var invalid = await SignOffWrite(server, path + "?limit=1", writer))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var invalid = await SignOffWrite(server, path, writer, reason: new string('a', 2001)))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var large = await SignOffWrite(server, path, writer, reason: new string('a', 20000)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        using (var media = await server.Send(HttpMethod.Post, path, new StringContent("reason"), writer, ifMatch: ["\"0\""]))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        var active = await SeedRerun(server, terminal: false);
        using (var pending = await SignOffWrite(server, SignOffPath(active.Snapshot.RunId), RerunOperator(server, active.Group)))
            Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        using (var noApproval = await SignOffWrite(server, path, writer, revoke: true))
            Assert.Equal(HttpStatusCode.Conflict, noApproval.StatusCode);
        using (var approved = await SignOffWrite(server, path, writer))
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using (var duplicate = await SignOffWrite(server, path, writer, "1"))
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        using (var denied = await SignOffWrite(server, path, writer, "1", revoke: true))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await server.Send(HttpMethod.Get, path, null!, writer))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(SignOffStatus.SignedOff, (await SignOffRead(server, path, viewer)).State.Status);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task SignOff_actual_PG_serializes_competing_writers_and_preserves_append_only_history()
    {
        await using var first = await Server.Start(database.DataSource, runs: true);
        await using var second = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(first);
        var path = SignOffPath(seed.Snapshot.RunId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
        {
            var server = index % 2 == 0 ? first : second;
            return SignOffWrite(server, path, RerunOperator(server, seed.Group));
        }));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.PreconditionFailed));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(1L, await Value("SELECT count(*) FROM run_signoff_events WHERE run_id=$1", seed.Snapshot.RunId));
        foreach (var statement in new[]
        {
            "UPDATE run_signoff_events SET reason='rewritten' WHERE run_id=$1",
            "DELETE FROM run_signoff_events WHERE run_id=$1"
        })
            await Assert.ThrowsAsync<PostgresException>(() => Execute(statement, seed.Snapshot.RunId));
        Assert.Single((await SignOffRead(first, path, RerunOperator(first, seed.Group))).History);
        Assert.Empty(first.Failures);
        Assert.Empty(second.Failures);
    }

    [Fact]
    public async Task SignOff_actual_PG_rolls_back_head_event_and_audit_together_on_storage_failure()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var bearer = RerunOperator(server, seed.Group);
        var path = SignOffPath(seed.Snapshot.RunId);
        await Execute("ALTER TABLE audit_log ADD CONSTRAINT reject_test_signoff CHECK(action <> 'run.signoff') NOT VALID");
        try
        {
            using var failed = await SignOffWrite(server, path, bearer);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal(0L, await Value("SELECT count(*) FROM run_signoffs WHERE run_id=$1", seed.Snapshot.RunId));
            Assert.Equal(0L, await Value("SELECT count(*) FROM run_signoff_events WHERE run_id=$1", seed.Snapshot.RunId));
            Assert.Equal(0L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1 AND action='run.signoff'", seed.Snapshot.RunId.ToString("D")));
        }
        finally { await Execute("ALTER TABLE audit_log DROP CONSTRAINT reject_test_signoff"); }
        using var retry = await SignOffWrite(server, path, bearer);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single((await SignOffRead(server, path, bearer)).History);
        Assert.Empty(server.Failures);
    }
}
