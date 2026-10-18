using System.Diagnostics;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Contracts.Definition;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Engine.Dispatch;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    public async Task VersionActivation_http_rollback_is_audited_refreshes_cache_and_preserves_pins_and_newest_baseline()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var first = await server.Post(request);
        var original = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        var newer = request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") };
        using var second = await server.Post(newer);
        var latest = (await Json(second)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => HasVersion(server.Versions, request.WorkflowId!, latest));
        var pinned = server.Versions.GetActive(request.WorkflowId!);
        var state = Initial(pinned);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        await store.CreateAsync(state, request.WorkflowId!, latest, DateTimeOffset.UtcNow.AddHours(1),
            TestContext.Current.CancellationToken);
        var before = await Value("SELECT xmin::text FROM workflow_versions WHERE id=$1", original);
        var clock = Stopwatch.StartNew();
        using var activated = await ActivateVersion(server, request, original);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal("no-store", activated.Headers.CacheControl?.ToString());
        var response = await Json(activated);
        Assert.Equal(request.WorkflowId, response.GetProperty("workflowId").GetString());
        Assert.Equal(original, response.GetProperty("activeVersionId").GetGuid());
        Assert.Equal(latest, response.GetProperty("latestVersionId").GetGuid());
        await WaitFor(() => HasVersion(server.Versions, request.WorkflowId!, original));
        Assert.True(clock.Elapsed <= TimeSpan.FromSeconds(5), $"Activation took {clock.Elapsed}.");
        Assert.Equal(latest, pinned.VersionId);
        Assert.Equal("1.0.1", pinned.Manifest.Version);
        Assert.Equal(latest, await Value("SELECT version_id FROM runs WHERE id=$1", state.Snapshot.RunId));
        using var trigger = await server.Send(HttpMethod.Post, "/api/v1/runs",
            JsonContent.Create(new TriggerRunRequest(request.WorkflowId!), RunJsonContext.Default.TriggerRunRequest),
            server.Token("Admin"));
        Assert.Equal(HttpStatusCode.Accepted, trigger.StatusCode);
        var run = (await Json(trigger)).GetProperty("runId").GetGuid();
        Assert.Equal(original.ToString(), await Value("SELECT payload->>'VersionId' FROM commands WHERE run_id=$1", run));
        Assert.Equal(before, await Value("SELECT xmin::text FROM workflow_versions WHERE id=$1", original));
        Assert.Equal(1L, await ActivationAudits(request.WorkflowId!));
        using (var audit = JsonDocument.Parse(Assert.IsType<string>(await Value("""
            SELECT summary::text FROM audit_log WHERE target_id=$1 AND action='workflow.activate'
            """, request.WorkflowId!))))
        {
            Assert.Equal(latest, audit.RootElement.GetProperty("before").GetProperty("activeVersionId").GetGuid());
            Assert.Equal(original, audit.RootElement.GetProperty("after").GetProperty("activeVersionId").GetGuid());
            Assert.Equal(latest, audit.RootElement.GetProperty("latestVersionId").GetGuid());
        }
        Assert.Equal(Server.Actor.ToString(), await Value("""
            SELECT actor FROM audit_log WHERE target_id=$1 AND action='workflow.activate'
            """, request.WorkflowId!));
        Assert.Equal("User", await Value("""
            SELECT actor_kind FROM audit_log WHERE target_id=$1 AND action='workflow.activate'
            """, request.WorkflowId!));
        var pointerXmin = await Value("SELECT xmin::text FROM workflows WHERE id=$1", request.WorkflowId!);
        using var repeated = await ActivateVersion(server, request, original);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(pointerXmin, await Value("SELECT xmin::text FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(1L, await ActivationAudits(request.WorkflowId!));
        foreach (var identical in new[] { request, newer })
        {
            using var unchanged = await server.Post(identical);
            Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
            Assert.Empty((await Json(unchanged)).EnumerateObject());
            Assert.Equal(original, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        }
        using var older = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "0.9.0") });
        Assert.Equal(HttpStatusCode.Conflict, older.StatusCode);
        Assert.Equal(original, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        using var merge = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.2") });
        Assert.Equal(HttpStatusCode.Created, merge.StatusCode);
        var merged = (await Json(merge)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => HasVersion(server.Versions, request.WorkflowId!, merged));
        Assert.Equal(merged, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(latest, await Value("SELECT version_id FROM runs WHERE id=$1", state.Snapshot.RunId));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, false, 403)]
    [InlineData(RegistryRole.Author, false, 403)]
    [InlineData(RegistryRole.Operator, false, 403)]
    [InlineData(RegistryRole.Admin, false, 200)]
    [InlineData(RegistryRole.Viewer, true, 403)]
    [InlineData(RegistryRole.Author, true, 403)]
    [InlineData(RegistryRole.Operator, true, 403)]
    [InlineData(RegistryRole.Admin, true, 200)]
    public async Task VersionActivation_requires_current_owning_team_or_type_Admin(RegistryRole role, bool typeScope, int expected)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        var registry = request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)!;
        var binding = new RegistryRoleBinding(role, group);
        registry = typeScope
            ? registry with { Types = [registry.Types[0] with { RoleBindings = [binding] }] }
            : registry with { RoleBindings = [binding] };
        request = request with { Registry = JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry) };
        using var first = await server.Post(request);
        var original = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        using var second = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
        var latest = (await Json(second)).GetProperty("workflowVersion").GetGuid();
        var bearer = server.Token("", "user", "api.read", groups: [group]);
        using var activated = await ActivateVersion(server, request, original, bearer);
        Assert.True(expected == (int)activated.StatusCode,
            $"Expected {expected}, got {(int)activated.StatusCode}. {string.Join("; ", server.Failures)}");
        Assert.Equal(expected == 200 ? original : latest,
            await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(expected == 200 ? 1L : 0L, await ActivationAudits(request.WorkflowId!));
        if (role != RegistryRole.Admin) return;
        using var revoke = await server.Post(new(request.GitCommit, request.GitAuthor,
            Registry: JsonSerializer.SerializeToElement(
                registry with { RoleBindings = [], Types = [registry.Types[0] with { RoleBindings = [] }] },
                TeamRegistryJsonContext.Default.TeamRegistry)));
        Assert.Equal(HttpStatusCode.Created, revoke.StatusCode);
        using var denied = await ActivateVersion(server, request, latest, bearer);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(original, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(1L, await ActivationAudits(request.WorkflowId!));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("registrar", 403)]
    [InlineData("foreign-admin", 403)]
    [InlineData("missing-actor", 403)]
    public async Task VersionActivation_rejects_untrusted_or_incomplete_principals_without_mutation(string kind, int expected)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var bearer = kind switch
        {
            "anonymous" => null,
            "registrar" => server.Token(),
            "missing-actor" => server.Token("Admin", includeActor: false),
            _ => server.Token("", "user", "api.read", groups: [Guid.NewGuid()])
        };
        using var response = await server.Send(HttpMethod.Post, ActivationPath(request, version), new ByteArrayContent([]),
            bearer, useDefaultToken: false);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0L, await ActivationAudits(request.WorkflowId!));
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("uppercase")]
    [InlineData("zero")]
    [InlineData("query")]
    [InlineData("body")]
    [InlineData("chunked")]
    public async Task VersionActivation_validates_exact_identity_and_empty_request_shape(string invalid)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var path = ActivationPath(request, version);
        path = invalid switch
        {
            "uuid" => path.Replace(version.ToString(), "not-a-uuid", StringComparison.Ordinal),
            "uppercase" => path.Replace(version.ToString(), version.ToString().ToUpperInvariant(), StringComparison.Ordinal),
            "zero" => path.Replace(version.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal),
            "query" => path + "?unexpected=true",
            _ => path
        };
        using HttpContent content = invalid == "chunked"
            ? new StreamContent(new ChunkedStream("{}"u8.ToArray()))
            : new ByteArrayContent(invalid == "body" ? "{}"u8.ToArray() : []);
        using var response = await server.Send(HttpMethod.Post, path, content, server.Token("Admin"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(0L, await ActivationAudits(request.WorkflowId!));
    }

    [Theory]
    [InlineData("missing-version", 404)]
    [InlineData("foreign-version", 404)]
    [InlineData("missing-workflow", 404)]
    [InlineData("deleted-workflow", 409)]
    [InlineData("corrupt-manifest", 500)]
    public async Task VersionActivation_rejects_unavailable_or_inconsistent_targets(string kind, int expected)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var active = version;
        var target = kind == "missing-version" ? Guid.CreateVersion7() : version;
        if (kind == "foreign-version")
        {
            var other = await Request(server);
            using var registration = await server.Post(other);
            target = (await Json(registration)).GetProperty("workflowVersion").GetGuid();
        }
        if (kind == "deleted-workflow")
            await Execute("UPDATE workflows SET deleted_at=now() WHERE id=$1", request.WorkflowId!);
        if (kind == "corrupt-manifest")
        {
            using var newer = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
            active = (await Json(newer)).GetProperty("workflowVersion").GetGuid();
            await Execute("UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{name}','\"different/release/workflow\"') WHERE id=$1", version);
        }
        var path = ActivationPath(request, target);
        if (kind == "missing-workflow") path = path.Replace("/workflow/", "/absent/", StringComparison.Ordinal);
        try
        {
            using var response = await server.Send(HttpMethod.Post, path, new ByteArrayContent([]), server.Token("Admin"));
            Assert.Equal(expected, (int)response.StatusCode);
            Assert.Equal(active, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
            Assert.Equal(0L, await ActivationAudits(request.WorkflowId!));
            Assert.Empty(server.Failures);
        }
        finally
        {
            if (kind == "corrupt-manifest")
                await Execute("UPDATE workflow_versions SET manifest=$2::jsonb WHERE id=$1", version, request.Manifest!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task VersionActivation_concurrent_identical_requests_commit_one_pointer_change_and_audit()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var first = await server.Post(request);
        var original = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        using var second = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
        var latest = (await Json(second)).GetProperty("workflowVersion").GetGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ActivateVersion(server, request, original)));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(original, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
            Assert.Equal(latest, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
            Assert.Equal(1L, await ActivationAudits(request.WorkflowId!));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task VersionActivation_and_registration_serialize_without_regressing_the_latest_baseline()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var first = await server.Post(request);
        var original = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        using var second = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
        var activating = ActivateVersion(server, request, original);
        var registering = server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.2") });
        await Task.WhenAll(activating, registering);
        using var activation = await activating;
        using var registration = await registering;
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var latest = (await Json(registration)).GetProperty("workflowVersion").GetGuid();
        Assert.Equal(latest, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        var active = Assert.IsType<Guid>(await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Contains(active, new[] { original, latest });
        await WaitFor(() => HasVersion(server.Versions, request.WorkflowId!, active));
        Assert.Equal(3L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(1L, await ActivationAudits(request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData(null, false, false, 409)]
    [InlineData(null, true, false, 409)]
    [InlineData(null, false, true, 200)]
    [InlineData("2.0.0", false, false, 200)]
    public async Task VersionActivation_checks_unpinned_schedule_variables_without_rolling_back_their_config(
        string? pin, bool paused, bool deleted, int expected)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var original = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        original = original with { Variables = [new("count", VariableType.Int, true, JsonSerializer.SerializeToElement(1))] };
        request = request with { Manifest = JsonSerializer.SerializeToElement(original, WorkflowManifestJsonContext.Default.WorkflowManifest) };
        using var first = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var oldId = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        var next = original with
        {
            Version = "2.0.0",
            Variables = [new("count", VariableType.String, true, JsonSerializer.SerializeToElement("new-default"))]
        };
        var schedules = new ScheduleFile([new("timer", "Etc/UTC", Interval: TimeSpan.FromMinutes(1), VersionPin: pin,
            Variables: ImmutableDictionary<string, JsonElement>.Empty.Add("count", JsonSerializer.SerializeToElement("activation-sensitive-value")))]);
        using var second = await server.Post(request with
        {
            Manifest = JsonSerializer.SerializeToElement(next, WorkflowManifestJsonContext.Default.WorkflowManifest),
            Schedules = JsonSerializer.Serialize(schedules, ScheduleFileJsonContext.Default.ScheduleFile),
            ScheduleSourcePath = "schedules.json"
        });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var latest = (await Json(second)).GetProperty("workflowVersion").GetGuid();
        await Execute("UPDATE schedules SET paused=$2,deleted_at=CASE WHEN $3 THEN now() ELSE NULL END WHERE workflow_id=$1",
            request.WorkflowId!, paused, deleted);
        var config = await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", request.WorkflowId!);
        var scheduleXmin = await Value("SELECT xmin::text FROM schedules WHERE workflow_id=$1", request.WorkflowId!);
        using var response = await ActivateVersion(server, request, oldId);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected == 200 ? oldId : latest,
            await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(config, await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(scheduleXmin, await Value("SELECT xmin::text FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(expected == 200 ? 1L : 0L, await ActivationAudits(request.WorkflowId!));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("activation-sensitive-value", body);
        if (expected == 409)
        {
            var problem = await Json(response);
            Assert.Equal("activation_schedule_conflict", problem.GetProperty("code").GetString());
            var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
            Assert.Contains(".variables", error.GetProperty("path").GetString());
        }
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task VersionActivation_audit_failure_rolls_back_pointer_and_notification()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        using var first = await server.Post(request);
        var original = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        using var second = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
        var latest = (await Json(second)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => HasVersion(server.Versions, request.WorkflowId!, latest));
        await using var listener = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var listen = new NpgsqlCommand("LISTEN registry", listener))
            await listen.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var notified = false;
        listener.Notification += (_, notification) => notified |= notification.Payload == request.WorkflowId;
        await Execute("ALTER TABLE audit_log ADD CONSTRAINT reject_test_activation CHECK(action <> 'workflow.activate') NOT VALID");
        try
        {
            using var rejected = await ActivateVersion(server, request, original);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal(latest, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
            Assert.Equal(latest, server.Versions.GetActive(request.WorkflowId!).VersionId);
            Assert.Equal(0L, await ActivationAudits(request.WorkflowId!));
            await listener.WaitAsync(150, TestContext.Current.CancellationToken);
            Assert.False(notified);
        }
        finally { await Execute("ALTER TABLE audit_log DROP CONSTRAINT reject_test_activation"); }
        using var accepted = await ActivateVersion(server, request, original);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await WaitFor(() => HasVersion(server.Versions, request.WorkflowId!, original));
        Assert.Equal(1L, await ActivationAudits(request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    internal static string ActivationPath(Wf.Contracts.Registration.RegistrationRequest request, Guid version) =>
        $"/api/v1/workflows/{request.WorkflowId}/versions/{version:D}/activate";

    internal static Task<HttpResponseMessage> ActivateVersion(Server server,
        Wf.Contracts.Registration.RegistrationRequest request, Guid version, string? bearer = null) =>
        server.Send(HttpMethod.Post, ActivationPath(request, version), new ByteArrayContent([]),
            bearer ?? server.Token("Admin", "user", "api.read"));

    private Task<object?> ActivationAudits(string workflow) => Value(
        "SELECT count(*) FROM audit_log WHERE target_id=$1 AND action='workflow.activate'", workflow);
}

public sealed partial class ScheduleFireTests
{
    [Fact]
    public async Task VersionActivation_during_active_schedules_preserves_every_fire_config_series_and_pinned_run()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server, "Concurrent");
        using var newer = await server.Post(schedule.Request with { Manifest = WithVersion(schedule.Request, "1.0.1") });
        var latest = (await Json(newer)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => server.Versions.GetActive(schedule.Workflow).VersionId == latest);
        var config = await Scalar("SELECT active_schedule_config_id FROM workflows WHERE id=$1", schedule.Workflow);
        var rowVersion = await Scalar("SELECT row_version FROM schedules WHERE id=$1", schedule.Id);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var timer = schedule.Timer;
        var slots = new List<DateTimeOffset>();
        var expected = new List<Guid>();
        for (var index = 0; index < 12; index++)
        {
            if (index is 3 or 7)
            {
                using var activation = await ActivateVersion(server, schedule.Request, index == 3 ? schedule.Version : latest);
                Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
            }
            slots.Add(timer.FireAt);
            expected.Add(index is >= 3 and < 7 ? schedule.Version : latest);
            server.Clock.Now = timer.FireAt;
            var current = timer;
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => engine.Scheduler.FireAsync(current, Token)));
            timer = Assert.IsType<Wf.ControlPlane.Engine.Scheduler.ScheduleTimer>(attempts[0]);
            Assert.All(attempts, attempt => Assert.Equal(timer, attempt));
        }
        var actualSlots = new List<DateTimeOffset>();
        var actualVersions = new List<Guid>();
        var runIds = new HashSet<Guid>();
        await using (var query = database.DataSource.CreateCommand("""
            SELECT f.fire_at,f.run_id,r.version_id,r.series_id,f.config_version_id,f.outcome
            FROM schedule_fires f JOIN runs r ON r.id=f.run_id AND r.run_day=uuid_v7_run_day(f.run_id)
            WHERE f.schedule_id=$1 ORDER BY f.fire_at
            """))
        {
            query.Parameters.AddWithValue(schedule.Id);
            await using var rows = await query.ExecuteReaderAsync(Token);
            while (await rows.ReadAsync(Token))
            {
                actualSlots.Add(rows.GetFieldValue<DateTimeOffset>(0));
                Assert.True(runIds.Add(rows.GetGuid(1)));
                actualVersions.Add(rows.GetGuid(2));
                Assert.Equal(schedule.Series, rows.GetGuid(3));
                Assert.Equal(config, rows.GetGuid(4));
                Assert.Equal((short)0, rows.GetInt16(5));
            }
        }
        Assert.Equal(slots, actualSlots);
        Assert.Equal(expected, actualVersions);
        Assert.Equal(12L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule.Id));
        Assert.Equal(12L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        Assert.Equal(config, await Scalar("SELECT active_schedule_config_id FROM workflows WHERE id=$1", schedule.Workflow));
        Assert.Equal(rowVersion, await Scalar("SELECT row_version FROM schedules WHERE id=$1", schedule.Id));
        Assert.Equal(timer.FireAt, await Scalar("SELECT next_fire_at FROM schedules WHERE id=$1", schedule.Id));
        Assert.Equal(latest, await Scalar("SELECT latest_version_id FROM workflows WHERE id=$1", schedule.Workflow));
        Assert.Empty(server.Failures);
    }
}
