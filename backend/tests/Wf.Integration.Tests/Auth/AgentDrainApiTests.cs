using System.Net;
using System.Net.Http.Json;
using System.Text;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Api.Machines;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task AgentDrainApi_action_crosses_the_real_http2_gateway_and_journaled_completion_is_transport_acknowledged()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var drains = new AgentDrainService(database.DataSource,
            new RunStore(database.DataSource, NullLogger<RunStore>.Instance), registry, TimeProvider.System);
        await using var gateway = await StartCalibrationGateway(registry, CalibrationServiceFor(registry), drains);
        using var channel = GrpcChannel.ForAddress(GatewayAddress(gateway));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: timeout.Token);
        var hello = MachineHello("drain-gateway-" + Guid.NewGuid().ToString("N"), "windows");
        hello.SupportsDrainCommands = true;
        await call.RequestStream.WriteAsync(new() { Sequence = 1, Hello = hello }, timeout.Token);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(1UL, call.ResponseStream.Current.Ack.Sequence);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using var response = await PostDrain(server, hello.Id, jwt);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        var drain = call.ResponseStream.Current.Drain;
        Assert.NotNull(drain);
        Assert.Equal(1UL, drain.ReservationEpoch);
        await call.RequestStream.WriteAsync(new() { Sequence = 2,
            DrainCompleted = new() { Id = drain.Id, ReservationEpoch = drain.ReservationEpoch } }, timeout.Token);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(2UL, call.ResponseStream.Current.Ack.Sequence);
        Assert.Equal("Released", (await drains.FindAsync(Guid.Parse(hello.Id), timeout.Token))!.Status);
        Assert.DoesNotContain(registry.DispatchableAgents(), agent => agent.Id.ToString("D") == hello.Id);
    }

    [Fact]
    public async Task AgentDrainApi_enforces_existing_Admin_boundary_strict_input_and_legacy_capability()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("drain-boundary-" + Guid.NewGuid().ToString("N"), "windows");
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        foreach (var role in new[] { "Viewer", "Operator", "Author", "" })
        {
            using var denied = await PostDrain(server, hello.Id, server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedRead = await server.GetPath($"/api/v1/machines/{hello.Id}/drain", server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        }
        using (var anonymous = await PostDrain(server, hello.Id, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var jwt = server.Token(role: "Admin");
        foreach (var body in new[] { "null", "{}", """{"maxDrainSeconds":1}""",
            """{"maxDrainSeconds":1,"reason":null}""", """{"maxDrainSeconds":0,"reason":"maintenance"}""",
            """{"maxDrainSeconds":86401,"reason":"maintenance"}""", """{"maxDrainSeconds":1,"reason":" "}""",
            """{"maxDrainSeconds":1,"reason":"maintenance","unknown":true}""",
            """{"maxDrainSeconds":1,"maxDrainSeconds":2,"reason":"maintenance"}""",
            "{\"maxDrainSeconds\":1,\"reason\":\"" + new string('x', 1025) + "\"}" })
        {
            using var invalid = await PostDrain(server, hello.Id, jwt, body);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var oversized = await PostDrain(server, hello.Id, jwt, new string(' ', 4097)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using (var wrongType = await server.GetPath($"/api/v1/machines/{hello.Id}/drain", jwt, HttpMethod.Post, new StringContent("{}")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        using (var invalidActor = await PostDrain(server, hello.Id, server.Token(user: Guid.Empty, role: "Admin")))
            Assert.Equal(HttpStatusCode.Forbidden, invalidActor.StatusCode);
        using (var legacy = await PostDrain(server, hello.Id, jwt))
            Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        using (var missing = await PostDrain(server, Guid.NewGuid().ToString("D"), jwt))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var audit = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_id=$1");
        audit.Parameters.AddWithValue(hello.Id);
        Assert.Equal(0L, await audit.ExecuteScalarAsync(MachineToken));
    }

    [Fact]
    public async Task AgentDrainApi_is_durable_idempotent_and_only_exact_current_session_physical_ack_releases()
    {
        var clock = new CalibrationClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var hello = MachineHello("drain-api-" + Guid.NewGuid().ToString("N"), "linux");
        hello.SupportsDrainCommands = true;
        var original = await registry.RegisterAsync(hello, MachineToken);
        var drains = new AgentDrainService(database.DataSource,
            new RunStore(database.DataSource, NullLogger<RunStore>.Instance), registry, clock);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using var response = await PostDrain(server, hello.Id, jwt);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync(AgentDrainJsonContext.Default.AgentDrainRecord, MachineToken))!;
        Assert.Equal("Draining", receipt.Status);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Null(receipt.ReleasedAt);
        using (var repeat = await PostDrain(server, hello.Id, jwt, """{"maxDrainSeconds":600,"reason":"changed"}"""))
        {
            var same = (await repeat.Content.ReadFromJsonAsync(AgentDrainJsonContext.Default.AgentDrainRecord, MachineToken))!;
            Assert.Equal(receipt, same);
        }
        await registry.LoadAsync(MachineToken);
        Assert.DoesNotContain(registry.DispatchableAgents(), agent => agent.Id == original.AgentId);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null((await drains.FindAsync(original.AgentId, MachineToken))!.ReleasedAt);
        var current = await registry.RegisterAsync(hello, MachineToken);
        Assert.NotEqual(original.SessionId, current.SessionId);
        await using (var advance = database.DataSource.CreateCommand("UPDATE agents SET reservation_epoch=2 WHERE id=$1"))
        {
            advance.Parameters.AddWithValue(current.AgentId);
            await advance.ExecuteNonQueryAsync(MachineToken);
        }
        var command = (await drains.PendingAsync(current, MachineToken))!;
        Assert.Equal(receipt.Id.ToString("D"), command.Id);
        Assert.Equal(2UL, command.ReservationEpoch);
        await drains.AcceptAsync(original, new() { Id = command.Id, ReservationEpoch = 2 }, MachineToken);
        await drains.AcceptAsync(current, new() { Id = command.Id, ReservationEpoch = 1 }, MachineToken);
        await drains.AcceptAsync(current, new() { Id = Guid.CreateVersion7().ToString("D"), ReservationEpoch = 2 }, MachineToken);
        Assert.Null((await drains.FindAsync(current.AgentId, MachineToken))!.ReleasedAt);
        await drains.AcceptAsync(current, new() { Id = command.Id, ReservationEpoch = 2 }, MachineToken);
        await drains.AcceptAsync(current, new() { Id = command.Id, ReservationEpoch = 2 }, MachineToken);
        Assert.Null(await drains.PendingAsync(current, MachineToken));
        using var status = await server.GetPath(response.Headers.Location!.OriginalString, jwt);
        var released = (await status.Content.ReadFromJsonAsync(AgentDrainJsonContext.Default.AgentDrainRecord, MachineToken))!;
        Assert.Equal("Released", released.Status);
        Assert.NotNull(released.ReleasedAt);
        Assert.DoesNotContain(registry.DispatchableAgents(), agent => agent.Id == current.AgentId);
        await using var audit = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_id=$1");
        audit.Parameters.AddWithValue(hello.Id);
        Assert.Equal(2L, await audit.ExecuteScalarAsync(MachineToken));
    }

    private static Task<HttpResponseMessage> PostDrain(Server server, string machine, string? jwt, string? body = null) =>
        server.GetPath($"/api/v1/machines/{machine}/drain", jwt, HttpMethod.Post,
            new StringContent(body ?? """{"maxDrainSeconds":1,"reason":"maintenance"}""", Encoding.UTF8, "application/json"));
}
