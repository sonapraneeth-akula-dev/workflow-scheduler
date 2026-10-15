using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class TriggerRunTests
{
    [Fact]
    public async Task TriggerIdempotency_same_key_returns_original_response_and_one_fenced_run()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var body = new TriggerRunRequest(request.WorkflowId!);
        var bearer = Operator(server, group);
        var key = Guid.NewGuid().ToString("D");
        var first = await AcceptedKey(server, body, bearer, key);
        Assert.Equal(first, await AcceptedKey(server, body, bearer, key));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1 AND status_code=202", key));
        Assert.Equal(first.ToString("D"), await Scalar("SELECT response->>'runId' FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(TimeSpan.FromHours(24), await Scalar("SELECT expires_at-created_at FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE id=$1", first));
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5200")),
            NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(Token);
            await recovery.RecoverAsync(Token);
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE id=$1", first));
            Assert.Equal(first, await AcceptedKey(server, body, bearer, key));
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1", first));
        }
        finally { await leader.StopAsync(Token); }
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task TriggerIdempotency_concurrent_retries_across_two_hosts_and_a_restart_create_one_command()
    {
        var request = default(Wf.Contracts.Registration.RegistrationRequest)!;
        var group = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        Guid expected;
        await using (var first = await Server.Start(database.DataSource, runs: true))
        await using (var second = await Server.Start(database.DataSource, runs: true))
        {
            request = await Request(first);
            var version = await Register(first, request, group);
            await Wait(() =>
            {
                if (!second.Versions.IsReady) return false;
                try { return second.Versions.GetActive(request.WorkflowId!).VersionId == version; }
                catch (KeyNotFoundException) { return false; }
            });
            var body = new TriggerRunRequest(request.WorkflowId!);
            var responses = await Task.WhenAll(Enumerable.Range(0, 48).Select(index =>
            {
                var server = index % 2 == 0 ? first : second;
                return AcceptedKey(server, body, Operator(server, group), key);
            }));
            expected = Assert.Single(responses.Distinct());
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
            Assert.Empty(first.Failures);
            Assert.Empty(second.Failures);
        }
        await using var restarted = await Server.Start(database.DataSource, runs: true);
        Assert.Equal(expected, await AcceptedKey(restarted, new(request.WorkflowId!), Operator(restarted, group), key));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task TriggerIdempotency_canonical_object_order_whitespace_and_omitted_defaults_replay_but_changed_request_conflicts()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var key = Guid.NewGuid().ToString("D");
        var bearer = Operator(server, group);
        var original = await AcceptedKey(server, new(request.WorkflowId!), bearer, key);
        var json = $" {{ \"variables\": {{}}, \"version\":null, \"workflowId\":{JsonSerializer.Serialize(request.WorkflowId)} }} ";
        using var equivalent = await server.Send(HttpMethod.Post, "/api/v1/runs",
            new StringContent(json, Encoding.UTF8, "application/json"), bearer, idempotencyKeys: [key]);
        Assert.Equal(HttpStatusCode.Accepted, equivalent.StatusCode);
        Assert.Equal(original, (await Json(equivalent)).GetProperty("runId").GetGuid());
        using var changedVersion = await Keyed(server, new(request.WorkflowId!, "1.0.0"), bearer, [key]);
        Assert.Equal(HttpStatusCode.Conflict, changedVersion.StatusCode);
        using var changedVariables = await Keyed(server, new(request.WorkflowId!)
        {
            Variables = System.Collections.Immutable.ImmutableDictionary<string, JsonElement>.Empty.Add("unknown", Element("\"must-not-echo\""))
        }, bearer, [key]);
        Assert.Equal(HttpStatusCode.Conflict, changedVariables.StatusCode);
        Assert.DoesNotContain("must-not-echo", await changedVariables.Content.ReadAsStringAsync(Token));
        var another = await Request(server);
        await Register(server, another, group);
        using var changedWorkflow = await Keyed(server, new(another.WorkflowId!), bearer, [key]);
        Assert.Equal(HttpStatusCode.Conflict, changedWorkflow.StatusCode);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1", original));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", another.WorkflowId!));
    }

    [Fact]
    public async Task TriggerIdempotency_replay_retains_original_version_during_activation_or_cache_outage_but_reauthorizes_revoked_access()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        var version = await Register(server, request, group);
        var bearer = Operator(server, group);
        var key = Guid.NewGuid().ToString("D");
        var original = await AcceptedKey(server, new(request.WorkflowId!), bearer, key);
        await Register(server, request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") }, group);
        await server.Versions.StopAsync(Token);
        Assert.Equal(original, await AcceptedKey(server, new(request.WorkflowId!), bearer, key));
        Assert.Equal(version, (await Payload(original)).GetProperty("VersionId").GetGuid());
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", group);
        using var revoked = await Keyed(server, new(request.WorkflowId!), bearer, [key]);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.DoesNotContain(original.ToString("D"), await revoked.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task TriggerIdempotency_same_key_is_scoped_to_principal_and_optional_header_leaves_requests_independent()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var key = Guid.NewGuid().ToString("D");
        var body = new TriggerRunRequest(request.WorkflowId!);
        var first = await AcceptedKey(server, body, Operator(server, group), key);
        var otherActor = Guid.NewGuid();
        var second = await AcceptedKey(server, body, server.Token("Operator", "user", "wf.read", groups: [group], actor: otherActor), key);
        Assert.NotEqual(first, second);
        Assert.Equal(otherActor.ToString("D"), await Scalar("SELECT created_by FROM commands WHERE run_id=$1", second));
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        var withoutKey = await Accepted(server, body, Operator(server, group));
        var anotherWithoutKey = await Accepted(server, body, Operator(server, group));
        Assert.Equal(4, new[] { first, second, withoutKey, anotherWithoutKey }.Distinct().Count());
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
    }

    [Fact]
    public async Task TriggerIdempotency_expires_at_exactly_24_hours_and_reused_key_can_identify_a_new_request()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var key = Guid.NewGuid().ToString("D");
        var bearer = Operator(server, group);
        var original = await AcceptedKey(server, new(request.WorkflowId!), bearer, key);
        var start = server.Clock.Now;
        server.Clock.Now = start.AddHours(24).AddMilliseconds(-1);
        Assert.Equal(original, await AcceptedKey(server, new(request.WorkflowId!), bearer, key));
        server.Clock.Now = start.AddHours(24);
        var next = await AcceptedKey(server, new(request.WorkflowId!, "1.0.0"), bearer, key);
        Assert.NotEqual(original, next);
        Assert.Equal(next, await AcceptedKey(server, new(request.WorkflowId!, "1.0.0"), bearer, key));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    [InlineData("two-headers")]
    [InlineData("too-long")]
    public async Task TriggerIdempotency_invalid_keys_are_400_without_insertion(string value)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var keys = value switch
        {
            "two-headers" => new[] { "first", "second" },
            "too-long" => [new string('a', 201)],
            _ => [value]
        };
        using var response = await Keyed(server, new(request.WorkflowId!), Operator(server, group), keys);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task TriggerIdempotency_variable_object_order_is_canonical_but_array_order_remains_significant()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [new("a", VariableType.Int), new("b", VariableType.StringList)]
        };
        request = request with { Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest) };
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var bearer = Operator(server, group);
        var key = Guid.NewGuid().ToString("D");
        var id = JsonSerializer.Serialize(request.WorkflowId);
        using var first = await Send($"{{\"workflowId\":{id},\"variables\":{{\"b\":[\"one\",\"two\"],\"a\":1}}}}");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var runId = (await Json(first)).GetProperty("runId").GetGuid();
        using var reordered = await Send($"{{\"variables\":{{\"a\":1,\"b\":[\"one\",\"two\"]}}, \"workflowId\":{id}}}");
        Assert.Equal(HttpStatusCode.Accepted, reordered.StatusCode);
        Assert.Equal(runId, (await Json(reordered)).GetProperty("runId").GetGuid());
        using var changed = await Send($"{{\"workflowId\":{id},\"variables\":{{\"b\":[\"two\",\"one\"],\"a\":1}}}}");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        Task<HttpResponseMessage> Send(string json) => server.Send(HttpMethod.Post, "/api/v1/runs",
            new StringContent(json, Encoding.UTF8, "application/json"), bearer, idempotencyKeys: [key]);
    }

    [Fact]
    public async Task TriggerIdempotency_accepts_exact_key_length_boundaries()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var bearer = Operator(server, group);
        var first = await AcceptedKey(server, new(request.WorkflowId!), bearer, "!");
        var last = await AcceptedKey(server, new(request.WorkflowId!), bearer, new string('~', 200));
        Assert.NotEqual(first, last);
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task TriggerIdempotency_validation_failure_rolls_back_reservation_and_fixed_request_can_reuse_the_key()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var key = Guid.NewGuid().ToString("D");
        var bearer = Operator(server, group);
        using var invalid = await Keyed(server, new(request.WorkflowId!, "missing"), bearer, [key]);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        _ = await AcceptedKey(server, new(request.WorkflowId!), bearer, key);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task TriggerIdempotency_response_persistence_failure_rolls_back_command_key_and_notification_atomically()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var key = Guid.NewGuid().ToString("D");
        await Execute("""
            CREATE FUNCTION reject_idempotency_response() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'forced response failure'; END $$;
            CREATE TRIGGER reject_idempotency_response BEFORE UPDATE ON idempotency_keys
                FOR EACH ROW EXECUTE FUNCTION reject_idempotency_response()
            """);
        await using var listener = await database.DataSource.OpenConnectionAsync(Token);
        var notifications = new List<string>();
        listener.Notification += (_, notification) =>
        {
            if (notification.Channel == "commands") notifications.Add(notification.Payload);
        };
        await using (var listen = new NpgsqlCommand("LISTEN commands", listener)) await listen.ExecuteNonQueryAsync(Token);
        try
        {
            using var response = await Keyed(server, new(request.WorkflowId!), Operator(server, group), [key]);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
            Assert.False(await listener.WaitAsync(100, Token));
            Assert.Empty(notifications);
            Assert.Single(server.Failures);
        }
        finally
        {
            await Execute("DROP TRIGGER reject_idempotency_response ON idempotency_keys; DROP FUNCTION reject_idempotency_response()");
        }
        var runId = await AcceptedKey(server, new(request.WorkflowId!), Operator(server, group), key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!notifications.Contains(runId.ToString("D"))) await listener.WaitAsync(timeout.Token);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE run_id=$1", runId));
    }

    private static Task<HttpResponseMessage> Keyed(Server server, TriggerRunRequest request, string bearer, string[] keys) =>
        server.Send(HttpMethod.Post, "/api/v1/runs", JsonContent.Create(request, RunJsonContext.Default.TriggerRunRequest),
            bearer, idempotencyKeys: keys);

    private static async Task<Guid> AcceptedKey(Server server, TriggerRunRequest request, string bearer, string key)
    {
        using var response = await Keyed(server, request, bearer, [key]);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, response.StatusCode + ": " + await response.Content.ReadAsStringAsync(Token));
        return (await Json(response)).GetProperty("runId").GetGuid();
    }
}
