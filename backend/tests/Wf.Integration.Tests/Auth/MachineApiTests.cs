using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api.Machines;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    private static CancellationToken MachineToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Machines_API_filters_actual_registered_metadata_and_stale_health_without_exposing_session_or_fences()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var pool = "machines-" + Guid.NewGuid().ToString("N");
        var healthy = MachineHello(pool, "linux", ("region", "eu"), ("kind", "gpu"));
        var stale = MachineHello(pool, "windows", ("region", "us"));
        var draining = MachineHello(pool, "linux", ("region", "eu"), ("kind", "cpu"));
        foreach (var hello in new[] { healthy, stale, draining }) await registry.RegisterAsync(hello, MachineToken);
        await using (var command = database.DataSource.CreateCommand("""
            UPDATE agents SET last_heartbeat_at=now()-interval '1 minute' WHERE id=$1;
            """))
        {
            command.Parameters.AddWithValue(Guid.Parse(stale.Id));
            await command.ExecuteNonQueryAsync(MachineToken);
        }
        await using (var command = database.DataSource.CreateCommand("UPDATE agents SET status=2 WHERE id=$1"))
        {
            command.Parameters.AddWithValue(Guid.Parse(draining.Id));
            await command.ExecuteNonQueryAsync(MachineToken);
        }
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using var all = await server.GetPath("/api/v1/machines?pool=" + pool, jwt);
        var page = await Machines(all);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(page.Items.Select(row => row.Id).Order(), page.Items.Select(row => row.Id));
        Assert.Equal("Unhealthy", Assert.Single(page.Items, row => row.Id == Guid.Parse(stale.Id)).Status);
        var live = Assert.Single(page.Items, row => row.Id == Guid.Parse(healthy.Id));
        Assert.Equal("Healthy", live.Status);
        Assert.Equal("x64", live.Arch);
        Assert.Equal(8192, live.RamMb);
        Assert.Equal("eu", live.Labels["region"]);
        Assert.Null(live.CurrentReservation);
        using (var exact = await server.GetPath("/api/v1/machines?pool=" + pool + "&os=linux&status=Healthy&label=region=eu&label=kind=gpu", jwt))
            Assert.Equal(Guid.Parse(healthy.Id), Assert.Single((await Machines(exact)).Items).Id);
        using (var drain = await server.GetPath("/api/v1/machines?pool=" + pool + "&status=Draining", jwt))
            Assert.Equal(Guid.Parse(draining.Id), Assert.Single((await Machines(drain)).Items).Id);
        using (var missing = await server.GetPath("/api/v1/machines?pool=" + pool + "&label=missing=value", jwt))
            Assert.Empty((await Machines(missing)).Items);
        var text = await all.Content.ReadAsStringAsync(MachineToken);
        foreach (var forbidden in new[] { "sessionId", "reservationEpoch", "labelsOverride", "uploadUri", "variables" })
            Assert.DoesNotContain(forbidden, text);
        foreach (var query in new[] { "?os=Linux", "?status=0", "?pool=a&pool=b", "?label=region", "?unknown=value" })
        {
            using var invalid = await server.GetPath("/api/v1/machines" + query, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task Machines_API_reuses_existing_global_Admin_boundary_and_rejects_Operator_patch_without_audit()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("boundary-" + Guid.NewGuid().ToString("N"), "linux");
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        foreach (var role in new[] { "", "Viewer", "Operator", "Author", "Agent", "Registrar", "admin" })
        {
            using var deniedRead = await server.GetPath("/api/v1/machines", server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
            Assert.True(deniedRead.Headers.CacheControl!.NoStore);
            using var deniedWrite = await PatchMachine(server, hello.Id, server.Token(role: role), """{"pool":"must-not-change"}""");
            Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        }
        using (var anonymous = await server.GetPath("/api/v1/machines", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var unsigned = await server.GetPath("/api/v1/machines", server.Token(role: "Admin", unsigned: true)))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var group = Guid.NewGuid();
        await Seed(RegistryRole.Admin, group, true);
        using (var teamAdmin = await server.GetPath("/api/v1/machines", server.Token(group: group)))
            Assert.Equal(HttpStatusCode.Forbidden, teamAdmin.StatusCode);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_id=$1");
        count.Parameters.AddWithValue(hello.Id);
        Assert.Equal(0L, await count.ExecuteScalarAsync(MachineToken));
    }

    [Fact]
    public async Task Machines_Admin_patch_is_atomic_audited_partial_and_survives_reconnect_and_registry_recreation()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("original-" + Guid.NewGuid().ToString("N"), "linux", ("old", "value"));
        var first = await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var actor = Guid.NewGuid();
        var jwt = server.Token(user: actor, role: "Admin");
        var pool = "edited-" + Guid.NewGuid().ToString("N");
        using (var response = await PatchMachine(server, hello.Id, jwt, JsonSerializer.Serialize(new { pool, labels = new { region = "eu" } })))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(hello.Pool, registry.Find(first.AgentId)!.Pool);
        var session = await registry.RegisterAsync(hello, MachineToken);
        Assert.NotEqual(first.SessionId, session.SessionId);
        Assert.Equal(pool, registry.Find(first.AgentId)!.Pool);
        Assert.Equal("eu", registry.Find(first.AgentId)!.Labels["region"]);
        Assert.False(registry.Find(first.AgentId)!.Labels.ContainsKey("old"));
        using (var clear = await PatchMachine(server, hello.Id, jwt, """{"labels":{}}"""))
            Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        using var recreated = new AgentRegistry(database.DataSource, TimeProvider.System);
        await recreated.RegisterAsync(hello, MachineToken);
        Assert.Empty(recreated.Find(first.AgentId)!.Labels);
        Assert.Equal(pool, recreated.Find(first.AgentId)!.Pool);
        using var read = await server.GetPath("/api/v1/machines?pool=" + pool, jwt);
        Assert.Empty(Assert.Single((await Machines(read)).Items).Labels);
        await using var audit = database.DataSource.CreateCommand("""
            SELECT actor,actor_kind,action,target_kind,team_id,summary::text FROM audit_log WHERE target_id=$1 ORDER BY id
            """);
        audit.Parameters.AddWithValue(hello.Id);
        await using var reader = await audit.ExecuteReaderAsync(MachineToken);
        Assert.True(await reader.ReadAsync(MachineToken));
        Assert.Equal(actor.ToString(), reader.GetString(0));
        Assert.Equal("User", reader.GetString(1));
        Assert.Equal("machine.update", reader.GetString(2));
        Assert.Equal("Machine", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        using var beforeAfter = JsonDocument.Parse(reader.GetString(5));
        Assert.Equal(hello.Pool, beforeAfter.RootElement.GetProperty("before").GetProperty("pool").GetString());
        Assert.Equal("value", beforeAfter.RootElement.GetProperty("before").GetProperty("labels").GetProperty("old").GetString());
        Assert.Equal(pool, beforeAfter.RootElement.GetProperty("after").GetProperty("pool").GetString());
        Assert.Equal("eu", beforeAfter.RootElement.GetProperty("after").GetProperty("labels").GetProperty("region").GetString());
        Assert.True(await reader.ReadAsync(MachineToken));
        Assert.False(await reader.ReadAsync(MachineToken));
    }

    [Fact]
    public async Task Machines_partial_override_does_not_pin_unsupplied_metadata_and_concurrent_edits_preserve_both_fields()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("partial-" + Guid.NewGuid().ToString("N"), "linux", ("old", "value"));
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var jwt = server.Token(identity: "app", scope: null, role: "Admin");
        using (var labels = await PatchMachine(server, hello.Id, jwt, """{"labels":{"pinned":"yes"}}"""))
            Assert.Equal(HttpStatusCode.NoContent, labels.StatusCode);
        hello.Pool += "-from-hello";
        await registry.RegisterAsync(hello, MachineToken);
        Assert.Equal(hello.Pool, registry.Find(Guid.Parse(hello.Id))!.Pool);
        var pool = "concurrent-" + Guid.NewGuid().ToString("N");
        var edits = await Task.WhenAll(
            PatchMachine(server, hello.Id, jwt, """{"labels":{"changed":"yes"}}"""),
            PatchMachine(server, hello.Id, jwt, JsonSerializer.Serialize(new { pool })));
        foreach (var edit in edits)
        {
            using (edit) Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        }
        await registry.RegisterAsync(hello, MachineToken);
        Assert.Equal(pool, registry.Find(Guid.Parse(hello.Id))!.Pool);
        Assert.Equal("yes", registry.Find(Guid.Parse(hello.Id))!.Labels["changed"]);
    }

    [Fact]
    public async Task Machines_audit_failure_rolls_back_metadata_and_overrides_and_does_not_echo_database_errors()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("rollback-" + Guid.NewGuid().ToString("N"), "linux");
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var trigger = "reject_machine_audit_" + Guid.NewGuid().ToString("N");
        await using var create = database.DataSource.CreateCommand($$"""
            CREATE FUNCTION {{trigger}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected-private-audit-detail'; END $$;
            CREATE TRIGGER {{trigger}} BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION {{trigger}}()
            """);
        await create.ExecuteNonQueryAsync(MachineToken);
        try
        {
            using var response = await PatchMachine(server, hello.Id, server.Token(role: "Admin"), """{"pool":"must-rollback","labels":{}}""");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("injected-private-audit-detail", await response.Content.ReadAsStringAsync(MachineToken));
            await using var command = database.DataSource.CreateCommand(
                "SELECT pool,labels_override IS NULL,pool_override IS NULL FROM agents WHERE id=$1");
            command.Parameters.AddWithValue(Guid.Parse(hello.Id));
            await using var reader = await command.ExecuteReaderAsync(MachineToken);
            Assert.True(await reader.ReadAsync(MachineToken));
            Assert.Equal(hello.Pool, reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }
        finally
        {
            await using var drop = database.DataSource.CreateCommand(
                $"DROP TRIGGER {trigger} ON audit_log; DROP FUNCTION {trigger}()");
            await drop.ExecuteNonQueryAsync(MachineToken);
        }
    }

    [Fact]
    public async Task Machines_current_reservation_remains_visible_when_expired_and_while_physical_fence_is_pending()
    {
        await using var scenario = await QueueScenario();
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using (var active = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, jwt))
        {
            var reservation = Assert.Single((await Machines(active)).Items).CurrentReservation!;
            Assert.Equal(scenario.Blocker.Id, reservation.Id);
            Assert.Equal(scenario.Owner.Snapshot.RunId, reservation.RunId);
            Assert.False(reservation.ReleasePending);
        }
        await using (var expire = database.DataSource.CreateCommand("""
            UPDATE reservations SET created_at=now()-interval '3 minutes',renewed_at=now()-interval '2 minutes',
                expires_at=now()-interval '1 minute' WHERE id=$1
            """))
        {
            expire.Parameters.AddWithValue(scenario.Blocker.Id);
            await expire.ExecuteNonQueryAsync(MachineToken);
        }
        using (var expired = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, jwt))
            Assert.Equal(scenario.Blocker.Id, Assert.Single((await Machines(expired)).Items).CurrentReservation!.Id);
        using var leases = new ReservationLeaseService(database.DataSource, scenario.Store, TimeProvider.System,
            NullLogger<ReservationLeaseService>.Instance);
        await leases.ReleaseAsync(scenario.Owner.Snapshot.RunId, "work", "Machine API fence test", MachineToken);
        using var pending = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, jwt);
        Assert.True(Assert.Single((await Machines(pending)).Items).CurrentReservation!.ReleasePending);
    }

    [Fact]
    public async Task Machines_patch_validates_canonical_identity_JSON_and_body_limits_before_any_mutation()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("invalid-" + Guid.NewGuid().ToString("N"), "linux");
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        foreach (var body in new[] { "null", "{}", """{"labels":null}""", """{"labels":{"x":1}}""",
            """{"pool":"one","pool":"two"}""", """{"labels":{"x":"one","x":"two"}}""",
            """{"status":"Draining"}""", "{" })
        {
            using var invalid = await PatchMachine(server, hello.Id, jwt, body);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var id in new[] { "invalid", Guid.Empty.ToString(), hello.Id.ToUpperInvariant(), hello.Id + "?unknown=true" })
        {
            using var invalid = await PatchMachine(server, id, jwt, """{"labels":{}}""");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var missing = await PatchMachine(server, Guid.NewGuid().ToString(), jwt, """{"labels":{}}"""))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var media = await server.GetPath("/api/v1/machines/" + hello.Id, jwt, HttpMethod.Patch,
                   new StringContent("""{"labels":{}}""")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        using (var oversized = await PatchMachine(server, hello.Id, jwt, new string(' ', 65537)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var chunked = new MachineChunkedContent(new string(' ', 65537));
        chunked.Headers.ContentType = new("application/json");
        using (var streamed = await server.GetPath("/api/v1/machines/" + hello.Id, jwt, HttpMethod.Patch, chunked))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        using (var invalidActor = await PatchMachine(server, hello.Id, server.Token(user: Guid.Empty, role: "Admin"), """{"labels":{}}"""))
            Assert.Equal(HttpStatusCode.Forbidden, invalidActor.StatusCode);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_id=$1");
        count.Parameters.AddWithValue(hello.Id);
        Assert.Equal(0L, await count.ExecuteScalarAsync(MachineToken));
    }

    [Fact]
    public async Task Machines_patch_affects_the_next_actual_dispatch_even_with_an_unchanged_leader_registry_cache()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("dispatch-old-" + Guid.NewGuid().ToString("N"), "linux", ("region", "old"));
        var session = await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var pool = "dispatch-new-" + Guid.NewGuid().ToString("N");
        using (var patch = await PatchMachine(server, hello.Id, server.Token(role: "Admin"),
                   JsonSerializer.Serialize(new { pool, labels = new { region = "new" } })))
            Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), TimeProvider.System);
        var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, "machine/test/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), MachineToken);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, MachineToken);
        await store.AppendAsync(run, EventEntity.Task, "work/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, MachineToken);
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var result = await dispatcher.DispatchAsync(run,
            new(run.Snapshot.RunId, "work", "task", 1, sha,
                new(new(pool: pool, labels: [new("region", "new")]))), new()
            {
                Task = new() { RunId = run.Snapshot.RunId.ToString(), NodeKey = "work", TaskKey = "task", Attempt = 1 },
                Sha256 = sha, Version = "1.0.0", PackageUri = "http://127.0.0.1/package.zip",
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
            }, MachineToken);
        Assert.Equal(session.AgentId, result.Agent!.Id);
        Assert.Equal(hello.Pool, registry.Find(session.AgentId)!.Pool);
        await using var committed = database.DataSource.CreateCommand("SELECT count(*) FROM dispatch_outbox WHERE run_id=$1");
        committed.Parameters.AddWithValue(run.Snapshot.RunId);
        Assert.Equal(1L, await committed.ExecuteScalarAsync(MachineToken));
    }

    private Task<Server> MachineServer() => Server.Start(database.DataSource,
        configure: app => app.MapMachineEndpoints(), configureServices: services => services.AddSingleton(TimeProvider.System));

    private static Hello MachineHello(string pool, string os, params (string Key, string Value)[] labels)
    {
        var id = Guid.NewGuid();
        var hello = new Hello
        {
            Id = id.ToString(), Name = "machine-" + id.ToString("N"), Pool = pool, Os = os, Arch = "x64",
            Cores = 4, RamMb = 8192, MaxTasks = 2, Version = "1.0.0", ProtocolVersion = 1
        };
        foreach (var (key, value) in labels) hello.Labels.Add(key, value);
        return hello;
    }

    private static Task<HttpResponseMessage> PatchMachine(Server server, string id, string jwt, string json) =>
        server.GetPath("/api/v1/machines/" + id, jwt, HttpMethod.Patch, new StringContent(json, Encoding.UTF8, "application/json"));

    private sealed class MachineChunkedContent(string text) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static async Task<MachineListResponse> Machines(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        return (await response.Content.ReadFromJsonAsync(MachineJsonContext.Default.MachineListResponse, MachineToken))!;
    }
}
