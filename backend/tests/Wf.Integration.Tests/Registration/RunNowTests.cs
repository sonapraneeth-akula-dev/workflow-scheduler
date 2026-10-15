using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class TriggerRunTests
{
    private const string RunNowFile = """
        {"defaults":{"build":{"env":"category","count":2}},
         "instances":[{"id":"morning","interval":"00:01:00","timezone":"Etc/UTC","variables":{"count":3}}]}
        """;

    [Fact]
    public async Task RunNow_uses_effective_instance_variables_and_permanent_series_without_advancing_schedule_or_fires()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (request, group, version, schedule, series) = await RunNowSchedule(server);
        var xmin = await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", schedule);
        var nextFire = await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule);
        var runId = await RunNowAccepted(server, schedule, Operator(server, group));
        var command = await Payload(runId);
        Assert.Equal(series, command.GetProperty("SeriesId").GetGuid());
        Assert.Equal(version, command.GetProperty("VersionId").GetGuid());
        Assert.Equal(request.WorkflowId, command.GetProperty("WorkflowId").GetString());
        var initial = command.GetProperty("InitialState");
        Assert.Equal("category", initial.GetProperty("Variables").GetProperty("env").GetString());
        Assert.Equal(3, initial.GetProperty("Variables").GetProperty("count").GetInt32());
        Assert.Equal("release", initial.GetProperty("Metadata").GetProperty("Type").GetString());
        Assert.Equal(xmin, await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", schedule));
        Assert.Equal(nextFire, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE id=$1", runId));
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5200")),
            NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(Token);
            await recovery.RecoverAsync(Token);
            Assert.Equal(series, await Scalar("SELECT series_id FROM runs WHERE id=$1", runId));
            Assert.Equal((short)0, await Scalar("SELECT trigger FROM runs WHERE id=$1", runId));
            Assert.Equal(version, await Scalar("SELECT version_id FROM runs WHERE id=$1", runId));
            Assert.Equal(Server.Actor.ToString("D"), await Scalar("SELECT triggered_by FROM runs WHERE id=$1", runId));
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE id=$1", runId));
            Assert.Equal(3, recovery.RecoveredRuns[runId].State.Snapshot.Variables["count"].GetInt32());
        }
        finally { await leader.StopAsync(Token); }
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task RunNow_resolves_active_exact_uuid_semver_range_and_wildcard_pins_without_rewriting_accepted_versions()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (request, group, original, schedule, _) = await RunNowSchedule(server);
        var firstRun = await RunNowAccepted(server, schedule, Operator(server, group));
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var nextRequest = request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest with { Version = "1.1.0" }, WorkflowManifestJsonContext.Default.WorkflowManifest)
        };
        var stable = await Register(server, nextRequest, group);
        var prerelease = await Register(server, request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest with { Version = "2.0.0-beta.2" }, WorkflowManifestJsonContext.Default.WorkflowManifest)
        }, group);
        foreach (var (pin, expected) in new (string?, Guid)[]
        {
            (null, prerelease), (original.ToString("D"), original), ("1.0.0+ignored-build", original),
            (">=1.0.0 <1.1.0", original), ("1.*", stable), ("*", stable),
            (">=2.0.0-alpha <2.0.0", prerelease)
        })
        {
            await SetPin(pin);
            var run = await RunNowAccepted(server, schedule, Operator(server, group));
            Assert.Equal(expected, (await Payload(run)).GetProperty("VersionId").GetGuid());
        }
        var huge = await Register(server, request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest with { Version = "999999999999999999999999.0.0" },
                WorkflowManifestJsonContext.Default.WorkflowManifest)
        }, group);
        await SetPin(">=2.0.0");
        var hugeRun = await RunNowAccepted(server, schedule, Operator(server, group));
        Assert.Equal(huge, (await Payload(hugeRun)).GetProperty("VersionId").GetGuid());
        Assert.Equal(original, (await Payload(firstRun)).GetProperty("VersionId").GetGuid());
        var foreign = await Request(server);
        var foreignVersion = await Register(server, foreign, Guid.NewGuid());
        foreach (var pin in new[] { "5.*", foreignVersion.ToString("D"), Guid.CreateVersion7().ToString("D") })
        {
            await SetPin(pin);
            using var missing = await RunNow(server, schedule, Operator(server, group));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        await SetPin("^1.0.0");
        using var unsupported = await RunNow(server, schedule, Operator(server, group));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Equal("$.schedule.versionPin", (await Json(unsupported)).GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Empty(server.Failures);

        Task SetPin(string? pin) => Execute("UPDATE schedules SET version_pin=$2 WHERE id=$1", schedule, (object?)pin ?? DBNull.Value);
    }

    [Theory]
    [InlineData("Author", RegistryRole.Author, "user")]
    [InlineData("Viewer", RegistryRole.Viewer, "user")]
    [InlineData("Registrar", RegistryRole.Admin, "app")]
    [InlineData("Operator", RegistryRole.Operator, "app")]
    public async Task RunNow_requires_scoped_operator_before_enqueuing(string role, RegistryRole binding, string identity)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (request, group, _, schedule, _) = await RunNowSchedule(server, binding);
        using var response = await RunNow(server, schedule, server.Token(role, identity,
            identity == "user" ? "wf.read" : null, groups: [group]));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("insufficient_scope_role", (await Json(response)).GetProperty("reason").GetString());
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task RunNow_rejects_cross_scope_anonymous_and_missing_actor_but_accepts_scoped_and_global_admin()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (_, group, _, schedule, _) = await RunNowSchedule(server, RegistryRole.Admin);
        using var anonymous = await server.Send(HttpMethod.Post, RunNowPath(schedule), new ByteArrayContent([]), useDefaultToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var crossScope = await RunNow(server, schedule, Operator(server, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, crossScope.StatusCode);
        using var missingActor = await RunNow(server, schedule, server.Token("Admin", includeActor: false));
        Assert.Equal(HttpStatusCode.Forbidden, missingActor.StatusCode);
        _ = await RunNowAccepted(server, schedule, Operator(server, group));
        _ = await RunNowAccepted(server, schedule, server.Token("Admin"));
    }

    [Fact]
    public async Task RunNow_paused_instances_and_user_origin_work_without_resuming_or_mutating_the_schedule()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (request, group, _, schedule, _) = await RunNowSchedule(server);
        await Execute("UPDATE schedules SET paused=true WHERE id=$1", schedule);
        var xmin = await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", schedule);
        _ = await RunNowAccepted(server, schedule, Operator(server, group));
        Assert.Equal(true, await Scalar("SELECT paused FROM schedules WHERE id=$1", schedule));
        Assert.Equal(xmin, await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", schedule));
        var userSchedule = Guid.CreateVersion7();
        var userSeries = Guid.CreateVersion7();
        await Execute("""
            INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,variables,created_by,updated_by)
            VALUES($1,$2,1,'UI schedule','Etc/UTC',60,'{"env":"ui","count":9}','test','test')
            """, userSchedule, request.WorkflowId!);
        await Execute("INSERT INTO run_series(id,workflow_id,schedule_id) VALUES($1,$2,$3)",
            userSeries, request.WorkflowId!, userSchedule);
        var userRun = await RunNowAccepted(server, userSchedule, Operator(server, group));
        var payload = await Payload(userRun);
        Assert.Equal(userSeries, payload.GetProperty("SeriesId").GetGuid());
        Assert.Equal("ui", payload.GetProperty("InitialState").GetProperty("Variables").GetProperty("env").GetString());
    }

    [Fact]
    public async Task RunNow_deduplicates_concurrent_retries_after_edits_and_removal_but_never_collides_with_other_instances()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (_, group, _, schedule, series) = await RunNowSchedule(server);
        var bearer = Operator(server, group);
        var key = Guid.NewGuid().ToString("D");
        var runs = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => RunNowAccepted(server, schedule, bearer, key)));
        var original = Assert.Single(runs.Distinct());
        await Execute("UPDATE schedules SET variables='{\"env\":\"edited\",\"count\":8}' WHERE id=$1", schedule);
        Assert.Equal(original, await RunNowAccepted(server, schedule, bearer, key));
        var next = await RunNowAccepted(server, schedule, bearer);
        Assert.NotEqual(original, next);
        Assert.Equal(3, (await Payload(original)).GetProperty("InitialState").GetProperty("Variables").GetProperty("count").GetInt32());
        Assert.Equal(8, (await Payload(next)).GetProperty("InitialState").GetProperty("Variables").GetProperty("count").GetInt32());
        Assert.Equal(series, (await Payload(next)).GetProperty("SeriesId").GetGuid());
        var (_, _, _, otherSchedule, _) = await RunNowSchedule(server);
        using var otherInstance = await RunNow(server, otherSchedule, server.Token("Admin"), key);
        Assert.Equal(HttpStatusCode.Conflict, otherInstance.StatusCode);
        await Execute("UPDATE schedules SET deleted_at=now(),paused=true,next_fire_at=NULL WHERE id=$1", schedule);
        Assert.Equal(original, await RunNowAccepted(server, schedule, bearer, key));
        using var deleted = await RunNow(server, schedule, bearer);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        using var unknown = await RunNow(server, Guid.CreateVersion7(), bearer);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM run_series WHERE id=$1", series));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"variables\":{\"count\":99}}")]
    [InlineData("{\"version\":\"1.0.0\"}")]
    [InlineData("{\"seriesId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task RunNow_rejects_body_overrides_or_invalid_shapes(string json)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (_, group, _, schedule, _) = await RunNowSchedule(server);
        using var response = await RunNow(server, schedule, Operator(server, group),
            content: new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task RunNow_enforces_exact_body_limits_and_streamed_limits_and_reports_bad_stored_variables_without_insertion()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (request, group, _, schedule, _) = await RunNowSchedule(server);
        var bearer = Operator(server, group);
        using (var media = await RunNow(server, schedule, bearer, content: new StringContent("{}")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        using (var exact = await RunNow(server, schedule, bearer,
            content: new StringContent("{}" + new string(' ', 4094), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.Accepted, exact.StatusCode);
        using (var over = await RunNow(server, schedule, bearer,
            content: new StringContent("{}" + new string(' ', 4095), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        using (var streamedContent = new StreamContent(new NonSeekingStream(Encoding.UTF8.GetBytes("{}" + new string(' ', 4095)))))
        {
            streamedContent.Headers.ContentType = new("application/json");
            using var streamed = await RunNow(server, schedule, bearer, content: streamedContent);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        }
        var before = await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!);
        await Execute("UPDATE schedules SET variables='{\"count\":\"must-not-echo\"}' WHERE id=$1", schedule);
        using var invalid = await RunNow(server, schedule, bearer);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("$.schedule.variables[\"count\"]", (await Json(invalid)).GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.DoesNotContain("must-not-echo", await invalid.Content.ReadAsStringAsync(Token));
        Assert.Equal(before, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    private async Task<(Wf.Contracts.Registration.RegistrationRequest Request, Guid Group, Guid Version, Guid Schedule, Guid Series)>
        RunNowSchedule(Server server, RegistryRole role = RegistryRole.Operator)
    {
        var request = await Request(server);
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [new("env", VariableType.String, true, Element("\"workflow\"")), new("count", VariableType.Int, true, Element("1"))]
        };
        request = request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest),
            Schedules = RunNowFile, ScheduleSourcePath = "schedules.json"
        };
        var group = Guid.NewGuid();
        var version = await Register(server, request, group, role);
        var schedule = Assert.IsType<Guid>(await Scalar("SELECT id FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        var series = Assert.IsType<Guid>(await Scalar("SELECT id FROM run_series WHERE schedule_id=$1", schedule));
        return (request, group, version, schedule, series);
    }

    private static string RunNowPath(Guid id) => "/api/v1/schedules/" + id.ToString("D") + "/run-now";

    private static Task<HttpResponseMessage> RunNow(Server server, Guid id, string bearer, string? key = null, HttpContent? content = null) =>
        server.Send(HttpMethod.Post, RunNowPath(id), content ?? new ByteArrayContent([]), bearer, idempotencyKeys: key is null ? null : [key]);

    private static async Task<Guid> RunNowAccepted(Server server, Guid id, string bearer, string? key = null)
    {
        using var response = await RunNow(server, id, bearer, key);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, response.StatusCode + ": " + await response.Content.ReadAsStringAsync(Token));
        return (await Json(response)).GetProperty("runId").GetGuid();
    }
}
