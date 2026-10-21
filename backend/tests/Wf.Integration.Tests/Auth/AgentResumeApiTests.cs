using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task AgentResumeApi_resumable_rollout_preflight_refuses_legacy_before_draining()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = ResumeHello();
        hello.ProcessInstanceId = "";
        var session = await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using (var legacy = await PostDrain(server, hello.Id, jwt,
            """{"maxDrainSeconds":60,"reason":"rollout","requireResumable":true}"""))
            Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        await registry.LoadAsync(MachineToken);
        Assert.Contains(registry.DispatchableAgents(), agent => agent.Id == session.AgentId);
        Assert.Null(await ResumeDrains(registry).FindAsync(session.AgentId, MachineToken));
        hello.ProcessInstanceId = Guid.NewGuid().ToString("D");
        await registry.RegisterAsync(hello, MachineToken);
        using var current = await PostDrain(server, hello.Id, jwt,
            """{"maxDrainSeconds":60,"reason":"rollout","requireResumable":true}""");
        Assert.Equal(HttpStatusCode.Accepted, current.StatusCode);
    }

    [Fact]
    public async Task AgentResumeApi_requires_a_restarted_process_not_a_reconnect_and_commits_one_audit()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = ResumeHello();
        var session = await registry.RegisterAsync(hello, MachineToken);
        var drains = ResumeDrains(registry);
        var receipt = await drains.RequestAsync(session.AgentId, 60, "release", Guid.NewGuid().ToString("D"), "User", MachineToken);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using (var pending = await PostResume(server, hello.Id, receipt.Id, jwt))
            Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        await drains.AcceptAsync(session, new() { Id = receipt.Id.ToString("D"), ReservationEpoch = 1 }, MachineToken);
        using (var sameProcess = await PostResume(server, hello.Id, receipt.Id, jwt))
            Assert.Equal(HttpStatusCode.Conflict, sameProcess.StatusCode);
        var reconnect = await registry.RegisterAsync(hello, MachineToken);
        Assert.NotEqual(session.SessionId, reconnect.SessionId);
        using (var reconnected = await PostResume(server, hello.Id, receipt.Id, jwt))
            Assert.Equal(HttpStatusCode.Conflict, reconnected.StatusCode);
        hello.ProcessInstanceId = Guid.NewGuid().ToString("D");
        await registry.RegisterAsync(hello, MachineToken);
        using (var resumed = await PostResume(server, hello.Id, receipt.Id, jwt))
        {
            Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
            Assert.True(resumed.Headers.CacheControl!.NoStore);
        }
        await registry.LoadAsync(MachineToken);
        Assert.Contains(registry.DispatchableAgents(), agent => agent.Id == session.AgentId);
        Assert.Null(await drains.FindAsync(session.AgentId, MachineToken));
        using (var repeated = await PostResume(server, hello.Id, receipt.Id, jwt))
            Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        await using (var audit = database.DataSource.CreateCommand("""
            SELECT count(*),min(summary->'before'->>'status'),min(summary->'after'->>'status'),
                min(summary->>'drainId') FROM audit_log WHERE target_id=$1 AND action='machine.resume'
            """))
        {
            audit.Parameters.AddWithValue(hello.Id);
            await using var reader = await audit.ExecuteReaderAsync(MachineToken);
            Assert.True(await reader.ReadAsync(MachineToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("Draining", reader.GetString(1));
            Assert.Equal("Healthy", reader.GetString(2));
            Assert.Equal(receipt.Id.ToString("D"), reader.GetString(3));
        }
        var next = await drains.RequestAsync(session.AgentId, 60, "next release", Guid.NewGuid().ToString("D"), "User", MachineToken);
        Assert.NotEqual(receipt.Id, next.Id);
        using var stale = await PostResume(server, hello.Id, receipt.Id, jwt);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(next.Id, (await drains.FindAsync(session.AgentId, MachineToken))!.Id);
    }

    [Fact]
    public async Task AgentResumeApi_process_proof_crosses_the_real_HTTP2_gateway_and_survives_re_registration()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var drains = ResumeDrains(registry);
        await using var gateway = await StartCalibrationGateway(registry, CalibrationServiceFor(registry), drains);
        using var channel = GrpcChannel.ForAddress(GatewayAddress(gateway));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = new AgentGateway.AgentGatewayClient(channel);
        var hello = ResumeHello();
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        Guid drainId;
        using (var first = client.Connect(cancellationToken: timeout.Token))
        {
            await first.RequestStream.WriteAsync(new() { Sequence = 1, Hello = hello }, timeout.Token);
            Assert.True(await first.ResponseStream.MoveNext(timeout.Token));
            Assert.Equal(1UL, first.ResponseStream.Current.Ack.Sequence);
            using var request = await PostDrain(server, hello.Id, jwt,
                """{"maxDrainSeconds":60,"reason":"HTTP2 maintenance","requireResumable":true}""");
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
            Assert.True(await first.ResponseStream.MoveNext(timeout.Token));
            var drain = first.ResponseStream.Current.Drain;
            Assert.NotNull(drain);
            drainId = Guid.Parse(drain.Id);
            await first.RequestStream.WriteAsync(new() { Sequence = 2,
                DrainCompleted = new() { Id = drain.Id, ReservationEpoch = drain.ReservationEpoch } }, timeout.Token);
            Assert.True(await first.ResponseStream.MoveNext(timeout.Token));
            Assert.Equal(2UL, first.ResponseStream.Current.Ack.Sequence);
        }
        hello.ProcessInstanceId = Guid.NewGuid().ToString("D");
        using var restarted = client.Connect(cancellationToken: timeout.Token);
        await restarted.RequestStream.WriteAsync(new() { Sequence = 1, Hello = hello }, timeout.Token);
        Assert.True(await restarted.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(1UL, restarted.ResponseStream.Current.Ack.Sequence);
        using var response = await PostResume(server, hello.Id, drainId, jwt);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await registry.LoadAsync(MachineToken);
        Assert.Contains(registry.DispatchableAgents(), agent => agent.Id.ToString("D") == hello.Id);
    }

    [Fact]
    public async Task AgentResumeApi_cannot_reenable_a_released_machine_with_assigned_attempts_or_live_reservations()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = ResumeHello();
        var session = await registry.RegisterAsync(hello, MachineToken);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, "machine/test/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), MachineToken);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, MachineToken);
        await store.AppendAsync(run, EventEntity.Task, "work/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, MachineToken);
        var reservations = new ReservationManager(database.DataSource, store, TimeProvider.System);
        var reservation = Assert.Single(await reservations.ReserveAsync(run.Snapshot.RunId, "work", 1,
            new(pool: hello.Pool), TimeSpan.FromMinutes(5), MachineToken));
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), TimeProvider.System);
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.Equal(session.AgentId, (await dispatcher.DispatchAsync(run,
            new(run.Snapshot.RunId, "work", "task", 1, sha, new(new(pool: hello.Pool))), new()
            {
                Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
                Sha256 = sha, Version = "1.0.0", PackageUri = "http://127.0.0.1/package.zip",
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
            }, MachineToken)).Agent!.Id);
        var drains = ResumeDrains(registry);
        var receipt = await drains.RequestAsync(session.AgentId, 60, "release", Guid.NewGuid().ToString("D"), "User", MachineToken);
        var command = (await drains.PendingAsync(session, MachineToken))!;
        await drains.AcceptAsync(session, new() { Id = receipt.Id.ToString("D"), ReservationEpoch = command.ReservationEpoch }, MachineToken);
        hello.ProcessInstanceId = Guid.NewGuid().ToString("D");
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        await using (var assigned = database.DataSource.CreateCommand("UPDATE task_attempts SET status=2 WHERE run_id=$1"))
        {
            assigned.Parameters.AddWithValue(run.Snapshot.RunId);
            Assert.Equal(1, await assigned.ExecuteNonQueryAsync(MachineToken));
        }
        using (var blocked = await PostResume(server, hello.Id, receipt.Id, jwt))
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await using (var lost = database.DataSource.CreateCommand("UPDATE task_attempts SET status=7 WHERE run_id=$1"))
        {
            lost.Parameters.AddWithValue(run.Snapshot.RunId);
            await lost.ExecuteNonQueryAsync(MachineToken);
        }
        await using (var held = database.DataSource.CreateCommand("UPDATE reservations SET released_at=NULL,release_reason=NULL WHERE id=$1"))
        {
            held.Parameters.AddWithValue(reservation.Id);
            Assert.Equal(1, await held.ExecuteNonQueryAsync(MachineToken));
        }
        using (var blocked = await PostResume(server, hello.Id, receipt.Id, jwt))
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await using (var released = database.DataSource.CreateCommand("UPDATE reservations SET released_at=clock_timestamp(),release_reason='Test release restored' WHERE id=$1"))
        {
            released.Parameters.AddWithValue(reservation.Id);
            await released.ExecuteNonQueryAsync(MachineToken);
        }
        using var resumed = await PostResume(server, hello.Id, receipt.Id, jwt);
        Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
    }

    [Theory]
    [InlineData("stale-heartbeat")]
    [InlineData("busy")]
    [InlineData("legacy-current")]
    [InlineData("legacy-release")]
    [InlineData("wrong-drain")]
    public async Task AgentResumeApi_refuses_ineligible_physical_capacity(string obstruction)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = ResumeHello();
        if (obstruction == "legacy-release") hello.ProcessInstanceId = "";
        var session = await registry.RegisterAsync(hello, MachineToken);
        var drains = ResumeDrains(registry);
        var receipt = await drains.RequestAsync(session.AgentId, 60, "release", Guid.NewGuid().ToString("D"), "User", MachineToken);
        await drains.AcceptAsync(session, new() { Id = receipt.Id.ToString("D"), ReservationEpoch = 1 }, MachineToken);
        hello.ProcessInstanceId = obstruction == "legacy-current" ? "" : Guid.NewGuid().ToString("D");
        await registry.RegisterAsync(hello, MachineToken);
        if (obstruction is "stale-heartbeat" or "busy")
        {
            await using var update = database.DataSource.CreateCommand(obstruction == "busy"
                ? "UPDATE agents SET slots_used=1 WHERE id=$1"
                : "UPDATE agents SET last_heartbeat_at=clock_timestamp()-interval '15 seconds' WHERE id=$1");
            update.Parameters.AddWithValue(session.AgentId);
            await update.ExecuteNonQueryAsync(MachineToken);
        }
        await using var server = await MachineServer();
        using var response = await PostResume(server, hello.Id,
            obstruction == "wrong-drain" ? Guid.CreateVersion7() : receipt.Id, server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Released", (await drains.FindAsync(session.AgentId, MachineToken))!.Status);
        await registry.LoadAsync(MachineToken);
        Assert.DoesNotContain(registry.DispatchableAgents(), agent => agent.Id == session.AgentId);
    }

    [Fact]
    public async Task AgentResumeApi_enforces_Admin_and_strict_bounded_input_before_mutation()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = ResumeHello();
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var id = Guid.CreateVersion7();
        foreach (var role in new[] { "Viewer", "Operator", "Author", "Agent", "" })
        {
            using var denied = await PostResume(server, hello.Id, id, server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var anonymous = await PostResume(server, hello.Id, id, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var jwt = server.Token(role: "Admin");
        foreach (var body in new[]
        {
            "null", "{}", """{"reason":"ready"}""", $$"""{"drainId":"{{id}}"}""",
            """{"drainId":"invalid","reason":"ready"}""",
            $$"""{"drainId":"{{Guid.Empty}}","reason":"ready"}""",
            $$"""{"drainId":"{{id}}","reason":null}""", $$"""{"drainId":"{{id}}","reason":" "}""",
            $$"""{"drainId":"{{id}}","reason":"ready","unknown":true}""",
            $$"""{"drainId":"{{id}}","drainId":"{{id}}","reason":"ready"}""",
            JsonSerializer.Serialize(new { drainId = id, reason = new string('x', 1025) }),
            JsonSerializer.Serialize(new { drainId = id, reason = new string('\u00e9', 513) })
        })
        {
            using var invalid = await PostResume(server, hello.Id, id, jwt, body);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var path in new[] { "/api/v1/machines/invalid/resume", $"/api/v1/machines/{Guid.Empty}/resume",
            $"/api/v1/machines/{hello.Id.ToUpperInvariant()}/resume", $"/api/v1/machines/{hello.Id}/resume?unknown=true" })
        {
            using var invalid = await server.GetPath(path, jwt, HttpMethod.Post,
                new StringContent(JsonSerializer.Serialize(new { drainId = id, reason = "ready" }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var missing = await PostResume(server, Guid.NewGuid().ToString("D"), id, jwt))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var invalidActor = await PostResume(server, hello.Id, id, server.Token(user: Guid.Empty, role: "Admin")))
            Assert.Equal(HttpStatusCode.Forbidden, invalidActor.StatusCode);
        using (var media = await server.GetPath($"/api/v1/machines/{hello.Id}/resume", jwt, HttpMethod.Post, new StringContent("{}")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        using (var oversized = await PostResume(server, hello.Id, id, jwt, new string(' ', 4097)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var chunked = new MachineChunkedContent(new string(' ', 4097));
        chunked.Headers.ContentType = new("application/json");
        using (var streamed = await server.GetPath($"/api/v1/machines/{hello.Id}/resume", jwt, HttpMethod.Post, chunked))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        await using var audit = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_id=$1");
        audit.Parameters.AddWithValue(hello.Id);
        Assert.Equal(0L, await audit.ExecuteScalarAsync(MachineToken));
    }

    [Fact]
    public async Task AgentResumeApi_audit_failure_rolls_back_status_and_retains_the_exact_drain()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = ResumeHello();
        var session = await registry.RegisterAsync(hello, MachineToken);
        var drains = ResumeDrains(registry);
        var receipt = await drains.RequestAsync(session.AgentId, 60, "release", Guid.NewGuid().ToString("D"), "User", MachineToken);
        await drains.AcceptAsync(session, new() { Id = receipt.Id.ToString("D"), ReservationEpoch = 1 }, MachineToken);
        hello.ProcessInstanceId = Guid.NewGuid().ToString("D");
        await registry.RegisterAsync(hello, MachineToken);
        var constraint = "resume_audit_" + Guid.NewGuid().ToString("N");
        await using var connection = await database.DataSource.OpenConnectionAsync(MachineToken);
        await using (var install = connection.CreateCommand())
        {
            install.CommandText = $"ALTER TABLE audit_log ADD CONSTRAINT {constraint} CHECK (action <> 'machine.resume' OR target_id <> '{hello.Id}') NOT VALID";
            await install.ExecuteNonQueryAsync(MachineToken);
        }
        try
        {
            await using var server = await MachineServer();
            using var failed = await PostResume(server, hello.Id, receipt.Id, server.Token(role: "Admin"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            await registry.LoadAsync(MachineToken);
            Assert.Equal(AgentStatus.Draining, registry.Find(session.AgentId)!.Status);
            Assert.Equal(receipt.Id, (await drains.FindAsync(session.AgentId, MachineToken))!.Id);
        }
        finally
        {
            await using var remove = connection.CreateCommand();
            remove.CommandText = $"ALTER TABLE audit_log DROP CONSTRAINT {constraint}";
            await remove.ExecuteNonQueryAsync(MachineToken);
        }
    }

    [Fact]
    public async Task AgentResumeApi_OpenAPI_exposes_the_typed_request_and_conflict_boundary()
    {
        await using var server = await MachineServer();
        using var response = await server.GetPath("/openapi/v1.json", server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(MachineToken));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/machines/{id}/resume").GetProperty("post");
        var reference = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
        Assert.EndsWith("/AgentResumeRequest", reference);
        Assert.True(operation.GetProperty("responses").TryGetProperty("204", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("409", out _));
    }

    private AgentDrainService ResumeDrains(AgentRegistry registry) => new(database.DataSource,
        new RunStore(database.DataSource, NullLogger<RunStore>.Instance), registry, TimeProvider.System);

    private static Hello ResumeHello()
    {
        var hello = MachineHello("resume-" + Guid.NewGuid().ToString("N"), "linux");
        hello.SupportsDrainCommands = true;
        hello.ProcessInstanceId = Guid.NewGuid().ToString("D");
        return hello;
    }

    private static Task<HttpResponseMessage> PostResume(Server server, string machine, Guid drain, string? jwt, string? body = null) =>
        server.GetPath($"/api/v1/machines/{machine}/resume", jwt, HttpMethod.Post,
            new StringContent(body ?? JsonSerializer.Serialize(new { drainId = drain, reason = "release verified" }), Encoding.UTF8, "application/json"));
}
