using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class TriggerRunTests(RegistrationDatabase database) : IClassFixture<RegistrationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TriggerRun_accepts_all_nine_types_defaults_metadata_and_atomic_command_notification_without_creating_a_run()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [
                new("text", VariableType.String, true), new("int", VariableType.Int, true),
                new("long", VariableType.Long, true), new("double", VariableType.Double, true),
                new("bool", VariableType.Bool, true), new("enum", VariableType.Enum, true, allowedValues: ["Dev", "Prod"]),
                new("list", VariableType.StringList, true), new("duration", VariableType.Duration, true),
                new("secret", VariableType.Secret, true), new("default", VariableType.String, true, Element("\"fallback\"")),
                new("override", VariableType.Int, true, Element("3")), new("optional", VariableType.String)]
        };
        request = request with { Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest) };
        var group = Guid.NewGuid();
        var version = await Register(server, request, group);
        var values = JsonSerializer.Deserialize<ImmutableDictionary<string, JsonElement>>("""
            {"text":"hello","int":42,"long":9223372036854775807,"double":1.5,"bool":false,
             "enum":"Dev","list":["a","b"],"duration":"1.02:03:04","secret":{"Name":"deploy-token"},"override":4}
            """)!;
        await using var listener = await database.DataSource.OpenConnectionAsync(Token);
        var notifications = new List<string>();
        listener.Notification += (_, notification) =>
        {
            if (notification.Channel == "commands") notifications.Add(notification.Payload);
        };
        await using (var listen = new NpgsqlCommand("LISTEN commands", listener)) await listen.ExecuteNonQueryAsync(Token);
        var runId = await Accepted(server, new(request.WorkflowId!) { Variables = values }, Operator(server, group));
        Assert.Equal(7, runId.Version);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!notifications.Contains(runId.ToString("D"))) await listener.WaitAsync(timeout.Token);
        var payload = await Payload(runId);
        Assert.Equal(version, payload.GetProperty("VersionId").GetGuid());
        Assert.Equal(request.WorkflowId, payload.GetProperty("WorkflowId").GetString());
        Assert.Equal(server.Clock.Now + manifest.RunTimeout, payload.GetProperty("DeadlineAt").GetDateTimeOffset());
        var initial = payload.GetProperty("InitialState");
        Assert.Equal(runId, initial.GetProperty("RunId").GetGuid());
        Assert.Equal((int)RunStatus.Queued, initial.GetProperty("Status").GetInt32());
        Assert.Equal("release", initial.GetProperty("Metadata").GetProperty("Type").GetString());
        Assert.Equal(0, initial.GetProperty("Nodes").GetProperty("work").GetInt32());
        Assert.Equal(0, initial.GetProperty("Tasks").GetProperty("work/task").GetInt32());
        var persisted = initial.GetProperty("Variables");
        foreach (var (key, value) in values)
            Assert.True(JsonElement.DeepEquals(value, persisted.GetProperty(key)), key);
        Assert.Equal("fallback", persisted.GetProperty("default").GetString());
        Assert.Equal(4, persisted.GetProperty("override").GetInt32());
        Assert.False(persisted.TryGetProperty("optional", out _));
        Assert.Equal(Server.Actor.ToString("D"), await Scalar("SELECT created_by FROM commands WHERE run_id=$1", runId));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE id=$1", runId));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1 AND processed_at IS NOT NULL", runId));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task TriggerRun_pins_active_or_exact_semver_or_uuid_and_never_rewrites_accepted_commands()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        var first = await Register(server, request, group);
        var bearer = Operator(server, group);
        var oldRun = await Accepted(server, new(request.WorkflowId!), bearer);
        var updated = request with { Manifest = Manifest(request.WorkflowId!, "1.0.1+build") };
        var latest = await Register(server, updated, group);
        var activeRun = await Accepted(server, new(request.WorkflowId!), bearer);
        var semverRun = await Accepted(server, new(request.WorkflowId!, "1.0.0+ignored-build"), bearer);
        var uuidRun = await Accepted(server, new(request.WorkflowId!, first.ToString("D")), bearer);
        Assert.Equal(first, (await Payload(oldRun)).GetProperty("VersionId").GetGuid());
        Assert.Equal(latest, (await Payload(activeRun)).GetProperty("VersionId").GetGuid());
        Assert.Equal(first, (await Payload(semverRun)).GetProperty("VersionId").GetGuid());
        Assert.Equal(first, (await Payload(uuidRun)).GetProperty("VersionId").GetGuid());
        var foreign = await Request(server);
        var foreignVersion = await Register(server, foreign, Guid.NewGuid());
        foreach (var version in new[] { foreignVersion.ToString("D"), Guid.CreateVersion7().ToString("D"), "9.9.9" })
        {
            using var response = await Trigger(server, new(request.WorkflowId!, version), bearer);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        foreach (var version in new[] { "", "latest", "01.0.0", new string('a', 257) })
        {
            using var response = await Trigger(server, new(request.WorkflowId!, version), bearer);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("$.version", (await Json(response)).GetProperty("errors")[0].GetProperty("path").GetString());
        }
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("Author", RegistryRole.Author, "user")]
    [InlineData("Viewer", RegistryRole.Viewer, "user")]
    [InlineData("Operator", RegistryRole.Operator, "app")]
    [InlineData("Registrar", RegistryRole.Admin, "app")]
    public async Task TriggerRun_denies_nonoperator_or_application_identities_before_version_and_variable_lookup(
        string role, RegistryRole binding, string identity)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group, binding);
        var bearer = server.Token(role, identity, identity == "user" ? "wf.read" : null, groups: [group]);
        using var response = await Trigger(server, new(request.WorkflowId!, "invalid-version"), bearer);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("insufficient_scope_role", (await Json(response)).GetProperty("reason").GetString());
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task TriggerRun_requires_authentication_actor_and_matching_scope_but_accepts_scoped_and_global_admin()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group, RegistryRole.Admin);
        using var anonymous = await server.Send(HttpMethod.Post, "/api/v1/runs",
            JsonContent.Create(new TriggerRunRequest(request.WorkflowId!), RunJsonContext.Default.TriggerRunRequest), useDefaultToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var missingActor = await Trigger(server, new(request.WorkflowId!),
            server.Token("Admin", includeActor: false));
        Assert.Equal(HttpStatusCode.Forbidden, missingActor.StatusCode);
        using var wrongGroup = await Trigger(server, new(request.WorkflowId!), Operator(server, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, wrongGroup.StatusCode);
        _ = await Accepted(server, new(request.WorkflowId!), Operator(server, group));
        _ = await Accepted(server, new(request.WorkflowId!), server.Token("Admin"));
        var other = await Request(server);
        await Register(server, other, Guid.NewGuid());
        using var crossTeam = await Trigger(server, new(other.WorkflowId!), Operator(server, group));
        Assert.Equal(HttpStatusCode.Forbidden, crossTeam.StatusCode);
    }

    [Fact]
    public async Task TriggerRun_reports_all_variable_paths_and_hints_without_echoing_values_or_inserting_commands()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [new("required", VariableType.String, true), new("typed", VariableType.Int),
                new("text", VariableType.String)]
        };
        request = request with { Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest) };
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var body = new TriggerRunRequest(request.WorkflowId!)
        {
            Variables = new Dictionary<string, JsonElement>
            {
                ["unknown"] = Element("1"), ["typed"] = Element("\"never-echo-this-value\""), ["text"] = Element("\"\\u0000\"")
            }.ToImmutableDictionary()
        };
        using var response = await Trigger(server, body, Operator(server, group));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var errors = (await Json(response)).GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(["$.variables[\"required\"]", "$.variables[\"text\"]", "$.variables[\"typed\"]", "$.variables[\"unknown\"]"],
            errors.Select(error => error.GetProperty("path").GetString()));
        Assert.All(errors, error =>
        {
            Assert.Equal("WFV025", error.GetProperty("ruleId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
        });
        Assert.DoesNotContain("never-echo-this-value", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"workflowId\":null}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"variables\":null}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"extra\":true}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"workflowId\":\"d/e/f\"}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"variables\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"placement\":{}}")]
    [InlineData("{\"workflowId\":\"a/b\"}")]
    [InlineData("{\"workflowId\":\"a/../c\"}")]
    [InlineData("{\"workflowId\":\"a/b/c\\\\d\"}")]
    [InlineData("{\"workflowId\":\"a/b/\\u0000\"}")]
    public async Task TriggerRun_rejects_invalid_ambiguous_or_unsupported_request_shapes_with_structured_400(string json)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        using var response = await Raw(server, json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task TriggerRun_enforces_media_and_actual_streamed_body_limits_and_cache_readiness()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        using (var wrongType = await server.Send(HttpMethod.Post, "/api/v1/runs", new StringContent("{}")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        using (var oversized = await Raw(server, "{\"workflowId\":\"" + new string('a', 1024 * 1024) + "\"}"))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using (var streamedContent = new StreamContent(new NonSeekingStream(Encoding.UTF8.GetBytes(
            "{\"workflowId\":\"" + new string('a', 1024 * 1024) + "\"}"))))
        {
            streamedContent.Headers.ContentType = new("application/json");
            using var streamed = await server.Send(HttpMethod.Post, "/api/v1/runs", streamedContent);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        }
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var json = JsonSerializer.Serialize(new TriggerRunRequest(request.WorkflowId!), RunJsonContext.Default.TriggerRunRequest);
        var boundary = json + new string(' ', 1024 * 1024 - Encoding.UTF8.GetByteCount(json));
        using (var exact = await server.Send(HttpMethod.Post, "/api/v1/runs",
            new StringContent(boundary, Encoding.UTF8, "application/json"), Operator(server, group)))
            Assert.Equal(HttpStatusCode.Accepted, exact.StatusCode);
        using (var over = await server.Send(HttpMethod.Post, "/api/v1/runs",
            new StringContent(boundary + " ", Encoding.UTF8, "application/json"), Operator(server, group)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        var beforeUnavailable = await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!);
        await server.Versions.StopAsync(Token);
        using var unavailable = await Trigger(server, new(request.WorkflowId!), Operator(server, group));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(beforeUnavailable, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task TriggerRun_authorized_unknown_or_soft_deleted_workflows_return_404_and_unicode_identity_matches_registration()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var bearer = Operator(server, group);
        using var unknown = await Trigger(server, new(request.WorkflowId! + "-missing"), bearer);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await Execute("""
            WITH updated AS (UPDATE workflows SET deleted_at=now() WHERE id=$1 RETURNING id)
            SELECT pg_notify('registry',id) FROM updated
            """, request.WorkflowId!);
        await Wait(() =>
        {
            try { server.Versions.GetActive(request.WorkflowId!); return false; }
            catch (KeyNotFoundException) { return true; }
        });
        using var deleted = await Trigger(server, new(request.WorkflowId!, "1.0.0"), bearer);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        var unicodeName = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1f680), 128));
        var workflowId = request.WorkflowId!;
        var teamType = workflowId[..workflowId.LastIndexOf('/')];
        var unicodeId = teamType + "/" + unicodeName;
        var unicodeRequest = request with { WorkflowId = unicodeId, Manifest = Manifest(unicodeId, "1.0.0") };
        await Register(server, unicodeRequest, group);
        _ = await Accepted(server, new(unicodeId), bearer);
        using var tooLong = await Trigger(server, new(unicodeId + "a"), bearer);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task TriggerRun_actual_leader_recovery_creates_the_selected_run_once_with_snapshot_deadline_and_principal()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        var version = await Register(server, request, group);
        var runId = await Accepted(server, new(request.WorkflowId!), Operator(server, group));
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5200")),
            NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
        using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
        await recovery.RecoverAsync(Token);
        await recovery.RecoverAsync(Token);
        Assert.True(recovery.IsReady);
        var recovered = recovery.RecoveredRuns[runId].State.Snapshot;
        Assert.Equal(RunStatus.Queued, recovered.Status);
        Assert.Equal("release", recovered.Metadata.Type);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE id=$1", runId));
        Assert.Equal(version, await Scalar("SELECT version_id FROM runs WHERE id=$1", runId));
        Assert.Equal(Server.Actor.ToString("D"), await Scalar("SELECT triggered_by FROM runs WHERE id=$1", runId));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1 AND processed_at IS NOT NULL", runId));
        var expected = (await Payload(runId)).GetProperty("DeadlineAt").GetDateTimeOffset();
        var actual = Assert.IsType<DateTime>(await Scalar("SELECT deadline_at FROM runs WHERE id=$1", runId));
        Assert.Equal(expected.Ticks / 10, actual.Ticks / 10);
        var loaded = await store.LoadAsync(runId, Token);
        Assert.Equal(recovered.Nodes, loaded.Snapshot.Nodes);
        await leader.StopAsync(Token);
        Assert.Empty(server.Failures);
    }

    private static string Operator(Server server, Guid group) => server.Token("Operator", "user", "wf.read", groups: [group]);

    private static Task<HttpResponseMessage> Trigger(Server server, TriggerRunRequest request, string bearer) =>
        server.Send(HttpMethod.Post, "/api/v1/runs", JsonContent.Create(request, RunJsonContext.Default.TriggerRunRequest), bearer);

    private static Task<HttpResponseMessage> Raw(Server server, string json) =>
        server.Send(HttpMethod.Post, "/api/v1/runs", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<Guid> Accepted(Server server, TriggerRunRequest request, string bearer)
    {
        using var response = await Trigger(server, request, bearer);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, response.StatusCode + ": " + await response.Content.ReadAsStringAsync(Token));
        return (await Json(response)).GetProperty("runId").GetGuid();
    }

    private static async Task<Guid> Register(Server server, RegistrationRequest request, Guid group,
        RegistryRole role = RegistryRole.Operator)
    {
        var registry = request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)! with
        {
            RoleBindings = [new(role, group)]
        };
        using var response = await server.Post(request with
        {
            Registry = JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry)
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, response.StatusCode + ": " + await response.Content.ReadAsStringAsync(Token));
        var version = (await Json(response)).GetProperty("workflowVersion").GetGuid();
        await Wait(() =>
        {
            if (!server.Versions.IsReady) return false;
            try { return server.Versions.GetActive(request.WorkflowId!).VersionId == version; }
            catch (KeyNotFoundException) { return false; }
        });
        return version;
    }

    private async Task<JsonElement> Payload(Guid runId) => Element(Assert.IsType<string>(
        await Scalar("SELECT payload::text FROM commands WHERE run_id=$1", runId)));

    private async Task<object?> Scalar(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        return await query.ExecuteScalarAsync(Token);
    }

    private async Task Execute(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        await query.ExecuteNonQueryAsync(Token);
    }

    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }
}
