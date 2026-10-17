using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api.Machines;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.ControlPlane.Engine.Reservations;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    private static readonly CapacityProfile CalibrationProfile = new("http", "json", "small");

    [Fact]
    public async Task CalibrationApi_enforces_Admin_strict_body_identity_limits_and_legacy_capability_without_mutation()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("calibration-boundary-" + Guid.NewGuid().ToString("N"), "linux");
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        foreach (var role in new[] { "Viewer", "Operator", "Author", "" })
        {
            using var denied = await PostCalibration(server, hello.Id, server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedRead = await server.GetPath($"/api/v1/machines/{hello.Id}/calibrations/{Guid.CreateVersion7():D}",
                server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        }
        using (var anonymous = await PostCalibration(server, hello.Id, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var jwt = server.Token(role: "Admin");
        foreach (var body in new[] { "null", "{}", """{"profile":null}""",
            """{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small","unknown":1}}""",
            """{"profile":{"transport":"http","transport":"grpc","codec":"json","payloadSizeClass":"small"}}""",
            """{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":100}""" })
        {
            using var bad = await PostCalibration(server, hello.Id, jwt, body);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        using (var legacy = await PostCalibration(server, hello.Id, jwt))
            Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        using (var missing = await PostCalibration(server, Guid.NewGuid().ToString("D"), jwt))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var malformed = await PostCalibration(server, "bad", jwt))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using (var invalidActor = await PostCalibration(server, hello.Id, server.Token(user: Guid.Empty, role: "Admin")))
            Assert.Equal(HttpStatusCode.Forbidden, invalidActor.StatusCode);
        using (var oversized = await PostCalibration(server, hello.Id, jwt, new string(' ', 65537)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var chunked = new MachineChunkedContent(new string(' ', 65537));
        chunked.Headers.ContentType = new("application/json");
        using (var oversized = await server.GetPath($"/api/v1/machines/{hello.Id}/calibrate", jwt, HttpMethod.Post, chunked))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using (var wrongType = await server.GetPath($"/api/v1/machines/{hello.Id}/calibrate", jwt, HttpMethod.Post, new StringContent("{}")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_id=$1");
        count.Parameters.AddWithValue(hello.Id);
        Assert.Equal(0L, await count.ExecuteScalarAsync(MachineToken));
    }

    [Fact]
    public async Task CalibrationApi_rejects_current_or_expired_but_unreleased_reservations()
    {
        await using var scenario = await QueueScenario();
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using var read = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, jwt);
        var machine = Assert.Single((await Machines(read)).Items);
        await using (var support = database.DataSource.CreateCommand("UPDATE agents SET supports_calibration_commands=true WHERE id=$1"))
        {
            support.Parameters.AddWithValue(machine.Id);
            await support.ExecuteNonQueryAsync(MachineToken);
        }
        using (var reserved = await PostCalibration(server, machine.Id.ToString("D"), jwt))
            Assert.Equal(HttpStatusCode.Conflict, reserved.StatusCode);
        await using (var expire = database.DataSource.CreateCommand("""
            UPDATE reservations SET created_at=created_at-interval '1 hour',renewed_at=renewed_at-interval '1 hour',
                expires_at=clock_timestamp()-interval '1 second' WHERE agent_id=$1 AND released_at IS NULL
            """))
        {
            expire.Parameters.AddWithValue(machine.Id);
            await expire.ExecuteNonQueryAsync(MachineToken);
        }
        using var expired = await PostCalibration(server, machine.Id.ToString("D"), jwt);
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
    }

    [Theory]
    [InlineData("slots")]
    [InlineData("stale")]
    [InlineData("draining")]
    [InlineData("unhealthy")]
    public async Task CalibrationApi_rejects_busy_stale_or_nonhealthy_machine(string state)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = CalibratableHello();
        await registry.RegisterAsync(hello, MachineToken);
        var mutation = state switch
        {
            "slots" => "slots_used=1", "stale" => "last_heartbeat_at=clock_timestamp()-interval '1 hour'",
            "draining" => "status=2", _ => "status=1"
        };
        await using (var update = database.DataSource.CreateCommand($"UPDATE agents SET {mutation} WHERE id=$1"))
        {
            update.Parameters.AddWithValue(Guid.Parse(hello.Id));
            await update.ExecuteNonQueryAsync(MachineToken);
        }
        await using var server = await MachineServer();
        using var response = await PostCalibration(server, hello.Id, server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CalibrationApi_positive_report_crosses_production_http2_gateway_and_commits_capacity_status_and_audit_once()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var calibrations = CalibrationServiceFor(registry);
        await using var gateway = await StartCalibrationGateway(registry, calibrations);
        using var channel = GrpcChannel.ForAddress(GatewayAddress(gateway));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: timeout.Token);
        var hello = CalibratableHello();
        await call.RequestStream.WriteAsync(new() { Sequence = 1, Hello = hello }, timeout.Token);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(1UL, call.ResponseStream.Current.Ack.Sequence);
        await using var server = await MachineServer();
        var actor = Guid.NewGuid();
        var jwt = server.Token(identity: "app", scope: null, user: actor, role: "Admin");
        using var response = await PostCalibration(server, hello.Id, jwt);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var receipt = (await response.Content.ReadFromJsonAsync(CalibrationJsonContext.Default.CalibrationRecord, timeout.Token))!;
        Assert.Equal("Queued", receipt.Status);
        Assert.Equal($"/api/v1/machines/{hello.Id}/calibrations/{receipt.Id:D}", response.Headers.Location!.OriginalString);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        var command = call.ResponseStream.Current.Calibrate;
        Assert.NotNull(command);
        Assert.Equal(receipt.Id.ToString("D"), command.Id);
        Assert.Equal(CalibrationProfile.ProfileKey, command.ProfileJson);
        Assert.Equal(receipt.Epoch, (long)command.ReservationEpoch);
        await call.RequestStream.WriteAsync(new() { Sequence = 2, CalibrationCompleted = CalibrationResult(receipt, 12345) }, timeout.Token);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(2UL, call.ResponseStream.Current.Ack.Sequence);
        using (var status = await server.GetPath(response.Headers.Location.OriginalString, jwt))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            var completed = (await status.Content.ReadFromJsonAsync(CalibrationJsonContext.Default.CalibrationRecord, timeout.Token))!;
            Assert.Equal("Succeeded", completed.Status);
            Assert.Equal(12345, completed.CapacityRps);
            Assert.True(completed.Applied);
            Assert.NotNull(completed.DispatchedAt);
            Assert.NotNull(completed.FinishedAt);
            Assert.Null(completed.Error);
        }
        await call.RequestStream.WriteAsync(new() { Sequence = 3, CalibrationCompleted = CalibrationResult(receipt, 99999) }, timeout.Token);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(3UL, call.ResponseStream.Current.Ack.Sequence);
        using (var read = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt))
        {
            var machine = Assert.Single((await Machines(read)).Items);
            Assert.Equal("Healthy", machine.Status);
            var capacity = Assert.Single(machine.Capacities);
            Assert.Equal("Calibrated", capacity.Source);
            Assert.Equal(12345, capacity.CapacityRps);
            Assert.NotNull(capacity.CalibratedAt);
        }
        await using var audits = database.DataSource.CreateCommand("""
            SELECT actor,actor_kind,action FROM audit_log WHERE target_id=$1 ORDER BY action
            """);
        audits.Parameters.AddWithValue(hello.Id);
        await using var reader = await audits.ExecuteReaderAsync(timeout.Token);
        foreach (var action in new[] { "machine.calibrate", "machine.calibration.completed" })
        {
            Assert.True(await reader.ReadAsync(timeout.Token));
            Assert.Equal(actor.ToString("D"), reader.GetString(0));
            Assert.Equal("App", reader.GetString(1));
            Assert.Equal(action, reader.GetString(2));
        }
        Assert.False(await reader.ReadAsync(timeout.Token));
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task CalibrationApi_native_AgentConnection_durably_reports_Unsupported_without_starting_load()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var calibrations = CalibrationServiceFor(registry);
        await using var gateway = await StartCalibrationGateway(registry, calibrations);
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "calibration-native", Guid.NewGuid().ToString("N")));
        var hello = CalibratableHello();
        var options = new AgentOptions([new Uri(GatewayAddress(gateway))], hello.Id, hello.Name, hello.Pool, 1,
            heartbeatInterval: TimeSpan.FromMilliseconds(100), resultJournalDirectory: Path.Combine(root, "journal"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
        stop.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var outbox = new AgentOutbox(new ResultJournal(options.ResultJournalDirectory, options.Id));
            using var cache = new PackageCache(Path.Combine(root, "cache"), NullLogger<PackageCache>.Instance);
            await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
            await using var commands = new TaskCommands(options, cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);
            using var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System, commands), commands,
                TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
            var running = connection.RunAsync(stop.Token);
            try
            {
                while (registry.Find(Guid.Parse(hello.Id)) is null) await Task.Delay(10, stop.Token);
                await using var server = await MachineServer();
                var jwt = server.Token(role: "Admin");
                using var requested = await PostCalibration(server, hello.Id, jwt);
                Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
                var receipt = (await requested.Content.ReadFromJsonAsync(CalibrationJsonContext.Default.CalibrationRecord, stop.Token))!;
                CalibrationRecord? completed;
                do
                {
                    await Task.Delay(10, stop.Token);
                    completed = await calibrations.FindAsync(receipt.AgentId, receipt.Id, stop.Token);
                } while (completed?.FinishedAt is null);
                Assert.Equal("Failed", completed.Status);
                Assert.Equal("Unsupported", completed.Error);
                Assert.Null(completed.CapacityRps);
                Assert.False(completed.Applied);
                Assert.Equal(0, hosts.ProcessStartCount);
                using var read = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt);
                Assert.Empty(Assert.Single((await Machines(read)).Items).Capacities);
            }
            finally { await stop.CancelAsync(); await running; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CalibrationApi_manual_override_wins_even_when_written_after_calibration_begins()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = CalibratableHello();
        var session = await registry.RegisterAsync(hello, MachineToken);
        var service = CalibrationServiceFor(registry);
        var request = await service.RequestAsync(session.AgentId, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        Assert.Single(await service.PendingAsync(session, MachineToken));
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using (var manual = await PatchMachine(server, hello.Id, jwt, ManualCapacityBody(CalibrationProfile, 777)))
            Assert.Equal(HttpStatusCode.NoContent, manual.StatusCode);
        await service.AcceptAsync(session, CalibrationResult(request, int.MaxValue), MachineToken);
        var status = (await service.FindAsync(session.AgentId, request.Id, MachineToken))!;
        Assert.Equal("Succeeded", status.Status);
        Assert.Equal(int.MaxValue, status.CapacityRps);
        Assert.False(status.Applied);
        using var read = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt);
        var effective = Assert.Single(Assert.Single((await Machines(read)).Items).Capacities);
        Assert.Equal(777, effective.CapacityRps);
        Assert.Equal("Manual", effective.Source);
        Assert.Null(effective.CalibratedAt);
    }

    [Fact]
    public async Task CalibrationApi_expiry_does_not_free_machine_and_reconnect_requires_current_session_and_epoch()
    {
        var clock = new CalibrationClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var hello = CalibratableHello();
        var first = await registry.RegisterAsync(hello, MachineToken);
        var service = CalibrationServiceFor(registry, clock);
        var request = await service.RequestAsync(first.AgentId, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        var pending = Assert.Single(await service.PendingAsync(first, MachineToken));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Single(await service.PendingAsync(first, MachineToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(first.AgentId, CalibrationProfile,
            Guid.NewGuid().ToString("D"), "User", MachineToken));
        var current = await registry.RegisterAsync(hello, MachineToken);
        Assert.Empty(await service.PendingAsync(first, MachineToken));
        Assert.Equal(pending, Assert.Single(await service.PendingAsync(current, MachineToken)));
        await service.AcceptAsync(first, CalibrationResult(request, 500), MachineToken);
        Assert.Null((await service.FindAsync(current.AgentId, request.Id, MachineToken))!.FinishedAt);
        var stale = CalibrationResult(request, 600);
        stale.ReservationEpoch++;
        await service.AcceptAsync(current, stale, MachineToken);
        Assert.Null((await service.FindAsync(current.AgentId, request.Id, MachineToken))!.FinishedAt);
        await service.AcceptAsync(current, CalibrationResult(request, 700), MachineToken);
        var completed = (await service.FindAsync(current.AgentId, request.Id, MachineToken))!;
        Assert.Equal("Failed", completed.Status);
        Assert.Equal("TimedOut", completed.Error);
        Assert.False(completed.Applied);
        Assert.Null(completed.CapacityRps);
        Assert.Equal(AgentStatus.Healthy, registry.Find(current.AgentId)!.Status);
        await using var capacities = database.DataSource.CreateCommand("SELECT count(*) FROM agent_capacities WHERE agent_id=$1");
        capacities.Parameters.AddWithValue(current.AgentId);
        Assert.Equal(0L, await capacities.ExecuteScalarAsync(MachineToken));
    }

    [Fact]
    public async Task CalibrationApi_explicit_terminal_failure_after_heartbeat_expiry_restores_Unhealthy()
    {
        var clock = new CalibrationClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(CalibratableHello(), MachineToken);
        var service = CalibrationServiceFor(registry, clock);
        var request = await service.RequestAsync(session.AgentId, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        clock.Advance(AgentRegistry.HeartbeatTimeout);
        var result = CalibrationResult(request, 0);
        result.Outcome = CalibrationOutcome.Unsupported;
        await service.AcceptAsync(session, result, MachineToken);
        Assert.Equal(AgentStatus.Unhealthy, registry.Find(session.AgentId)!.Status);
    }

    [Fact]
    public async Task CalibrationApi_concurrent_requests_serialize_and_audit_failure_rolls_back_draining_job_and_epoch()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = CalibratableHello();
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        var trigger = "reject_calibration_" + Guid.NewGuid().ToString("N");
        await using (var create = database.DataSource.CreateCommand($$"""
            CREATE FUNCTION {{trigger}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.action='machine.calibrate' THEN RAISE EXCEPTION 'private-calibration-audit-failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER {{trigger}} BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION {{trigger}}()
            """))
            await create.ExecuteNonQueryAsync(MachineToken);
        try
        {
            using var response = await PostCalibration(server, hello.Id, jwt);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("private-calibration-audit-failure", await response.Content.ReadAsStringAsync(MachineToken));
            await using var query = database.DataSource.CreateCommand("""
                SELECT status,reservation_epoch,(SELECT count(*) FROM calibration_requests WHERE agent_id=$1)
                FROM agents WHERE id=$1
                """);
            query.Parameters.AddWithValue(Guid.Parse(hello.Id));
            await using var reader = await query.ExecuteReaderAsync(MachineToken);
            Assert.True(await reader.ReadAsync(MachineToken));
            Assert.Equal(0, reader.GetInt16(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(0L, reader.GetInt64(2));
        }
        finally
        {
            await using var drop = database.DataSource.CreateCommand($"DROP TRIGGER {trigger} ON audit_log; DROP FUNCTION {trigger}()");
            await drop.ExecuteNonQueryAsync(MachineToken);
        }
        var responses = await Task.WhenAll(PostCalibration(server, hello.Id, jwt), PostCalibration(server, hello.Id, jwt));
        try
        {
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Accepted);
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var item in responses) item.Dispose(); }
    }

    [Fact]
    public async Task CalibrationApi_leader_fence_rejects_old_engine_before_dispatch_or_capacity_commit()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(CalibratableHello(), MachineToken);
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("D"), new Uri("http://localhost:5998")),
            NullLogger<LeaseManager>.Instance);
        var runs = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
        var service = new CalibrationService(database.DataSource, runs, registry, TimeProvider.System);
        var request = await service.RequestAsync(session.AgentId, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        Assert.True(await leader.TryAcquireOrRenewAsync(MachineToken));
        await using (var expire = database.DataSource.CreateCommand("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second' WHERE name='engine'"))
            await expire.ExecuteNonQueryAsync(MachineToken);
        using var next = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("D"), new Uri("http://localhost:5999")),
            NullLogger<LeaseManager>.Instance);
        Assert.True(await next.TryAcquireOrRenewAsync(MachineToken));
        try
        {
            await Assert.ThrowsAsync<NotLeaderException>(() => service.PendingAsync(session, MachineToken));
            await Assert.ThrowsAsync<NotLeaderException>(() => service.AcceptAsync(session, CalibrationResult(request, 100), MachineToken));
            var record = (await service.FindAsync(session.AgentId, request.Id, MachineToken))!;
            Assert.Equal("Queued", record.Status);
            Assert.Null(record.DispatchedAt);
            Assert.Null(record.FinishedAt);
        }
        finally { await next.ReleaseAsync(MachineToken); }
    }

    [Fact]
    public async Task CalibrationApi_authoritative_Draining_blocks_actual_dispatch_with_stale_Healthy_leader_cache()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = CalibratableHello();
        var session = await registry.RegisterAsync(hello, MachineToken);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), TimeProvider.System);
        await CalibrationServiceFor(registry).RequestAsync(session.AgentId, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        Assert.Equal(AgentStatus.Healthy, registry.Find(session.AgentId)!.Status);
        var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, "machine/calibration/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), MachineToken);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, MachineToken);
        await store.AppendAsync(run, EventEntity.Task, "work/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, MachineToken);
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var result = await dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, "work", "task", 1, sha, new(new(pool: hello.Pool))),
            new()
            {
                Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
                Sha256 = sha, Version = "1.0.0", PackageUri = "http://127.0.0.1/package.zip",
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
            }, MachineToken);
        Assert.Null(result.Agent);
        await using var committed = database.DataSource.CreateCommand("SELECT count(*) FROM dispatch_outbox WHERE run_id=$1");
        committed.Parameters.AddWithValue(run.Snapshot.RunId);
        Assert.Equal(0L, await committed.ExecuteScalarAsync(MachineToken));
        Assert.True(await registry.HeartbeatAsync(session, new() { At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) }, MachineToken));
        Assert.Equal(AgentStatus.Draining, registry.Find(session.AgentId)!.Status);
    }

    [Theory]
    [InlineData(CapacitySource.Manual, false)]
    [InlineData(CapacitySource.Calibrated, true)]
    public async Task CalibrationApi_updates_only_the_measured_profile_and_preserves_preexisting_manual_priority(
        CapacitySource existing, bool applied)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(CalibratableHello(), MachineToken);
        var other = new CapacityProfile("http", "json", "large");
        await InsertMachineCapacity(session.AgentId, CalibrationProfile, 200, existing,
            existing == CapacitySource.Manual ? null : DateTimeOffset.UnixEpoch);
        await InsertMachineCapacity(session.AgentId, other, 900, CapacitySource.Calibrated, DateTimeOffset.UnixEpoch);
        var service = CalibrationServiceFor(registry);
        var request = await service.RequestAsync(session.AgentId, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        await service.PendingAsync(session, MachineToken);
        await service.AcceptAsync(session, CalibrationResult(request, 1000), MachineToken);
        Assert.Empty(await service.PendingAsync(session, MachineToken));
        Assert.Equal(applied, (await service.FindAsync(session.AgentId, request.Id, MachineToken))!.Applied);
        await using var query = database.DataSource.CreateCommand("""
            SELECT profile_key,capacity_rps,source,calibrated_at FROM agent_capacities WHERE agent_id=$1 ORDER BY profile_key
            """);
        query.Parameters.AddWithValue(session.AgentId);
        await using var reader = await query.ExecuteReaderAsync(MachineToken);
        var count = 0;
        while (await reader.ReadAsync(MachineToken))
        {
            count++;
            if (reader.GetString(0) == other.ProfileKey)
            {
                Assert.Equal(900, reader.GetInt32(1));
                Assert.Equal(DateTimeOffset.UnixEpoch, reader.GetFieldValue<DateTimeOffset>(3));
            }
            else
            {
                Assert.Equal(applied ? 1000 : 200, reader.GetInt32(1));
                Assert.Equal((short)existing, reader.GetInt16(2));
                if (applied) Assert.True(reader.GetFieldValue<DateTimeOffset>(3) > DateTimeOffset.UnixEpoch);
                else Assert.True(reader.IsDBNull(3));
            }
        }
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CalibrationApi_completion_audit_failure_rolls_back_capacity_result_and_machine_until_durable_retry()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(CalibratableHello(), MachineToken);
        var service = CalibrationServiceFor(registry);
        var request = await service.RequestAsync(session.AgentId, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        await service.PendingAsync(session, MachineToken);
        var trigger = "reject_calibration_result_" + Guid.NewGuid().ToString("N");
        await using (var create = database.DataSource.CreateCommand($$"""
            CREATE FUNCTION {{trigger}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.action='machine.calibration.completed' THEN RAISE EXCEPTION 'completion audit failed'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER {{trigger}} BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION {{trigger}}()
            """))
            await create.ExecuteNonQueryAsync(MachineToken);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => service.AcceptAsync(session, CalibrationResult(request, 500), MachineToken));
            var record = (await service.FindAsync(session.AgentId, request.Id, MachineToken))!;
            Assert.Equal("Dispatched", record.Status);
            Assert.Null(record.FinishedAt);
            await using var query = database.DataSource.CreateCommand("""
                SELECT status,(SELECT count(*) FROM agent_capacities WHERE agent_id=$1) FROM agents WHERE id=$1
                """);
            query.Parameters.AddWithValue(session.AgentId);
            await using var reader = await query.ExecuteReaderAsync(MachineToken);
            Assert.True(await reader.ReadAsync(MachineToken));
            Assert.Equal(2, reader.GetInt16(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }
        finally
        {
            await using var drop = database.DataSource.CreateCommand($"DROP TRIGGER {trigger} ON audit_log; DROP FUNCTION {trigger}()");
            await drop.ExecuteNonQueryAsync(MachineToken);
        }
        var invalid = CalibrationResult(request, 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.AcceptAsync(session, invalid, MachineToken));
        Assert.Null((await service.FindAsync(session.AgentId, request.Id, MachineToken))!.FinishedAt);
        await service.AcceptAsync(session, CalibrationResult(request, 500), MachineToken);
        Assert.True((await service.FindAsync(session.AgentId, request.Id, MachineToken))!.Applied);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CalibrationApi_durable_active_attempt_blocks_calibration_even_before_physical_heartbeat_reports_slots(short status)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = CalibratableHello();
        var session = await registry.RegisterAsync(hello, MachineToken);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), TimeProvider.System);
        var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, "machine/calibration/busy", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), MachineToken);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, MachineToken);
        await store.AppendAsync(run, EventEntity.Task, "work/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, MachineToken);
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var placed = await dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, "work", "task", 1, sha, new(new(pool: hello.Pool))),
            new()
            {
                Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
                Sha256 = sha, Version = "1.0.0", PackageUri = "http://127.0.0.1/package.zip",
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
            }, MachineToken);
        Assert.Equal(session.AgentId, placed.Agent!.Id);
        await using (var update = database.DataSource.CreateCommand("UPDATE task_attempts SET status=$2 WHERE run_id=$1"))
        {
            update.Parameters.AddWithValue(run.Snapshot.RunId);
            update.Parameters.AddWithValue(status);
            await update.ExecuteNonQueryAsync(MachineToken);
        }
        await using var server = await MachineServer();
        using var response = await PostCalibration(server, hello.Id, server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CalibrationApi_Draining_also_blocks_reservation_acquisition_and_terminal_failure_restores_it()
    {
        await using var scenario = await QueueScenario();
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        await registry.LoadAsync(MachineToken);
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        using var read = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, jwt);
        var machine = Assert.Single((await Machines(read)).Items);
        await scenario.ReleaseOwner();
        await using (var support = database.DataSource.CreateCommand("UPDATE agents SET supports_calibration_commands=true WHERE id=$1"))
        {
            support.Parameters.AddWithValue(machine.Id);
            await support.ExecuteNonQueryAsync(MachineToken);
        }
        var service = CalibrationServiceFor(registry);
        var request = await service.RequestAsync(machine.Id, CalibrationProfile, Guid.NewGuid().ToString("D"), "User", MachineToken);
        var blocked = await scenario.Manager.RequestAsync(scenario.First.Snapshot.RunId, "work", 1,
            new(pool: scenario.Pool), TimeSpan.FromMinutes(2), cancellationToken: MachineToken);
        Assert.Empty(blocked.Reservations);
        var failed = CalibrationResult(request, 0);
        failed.Outcome = CalibrationOutcome.Unsupported;
        var session = registry.Find(machine.Id)!;
        await service.AcceptAsync(new(machine.Id, session.SessionId), failed, MachineToken);
        var restored = await scenario.Manager.RequestAsync(scenario.First.Snapshot.RunId, "work", 1,
            new(pool: scenario.Pool), TimeSpan.FromMinutes(2), cancellationToken: MachineToken);
        Assert.Single(restored.Reservations);
    }

    private CalibrationService CalibrationServiceFor(AgentRegistry registry, TimeProvider? time = null) =>
        new(database.DataSource, new RunStore(database.DataSource, NullLogger<RunStore>.Instance), registry, time ?? TimeProvider.System);

    private static Hello CalibratableHello()
    {
        var hello = MachineHello("calibration-" + Guid.NewGuid().ToString("N"), "linux");
        hello.SupportsCalibrationCommands = true;
        return hello;
    }

    private static CalibrationCompleted CalibrationResult(CalibrationRecord record, int rate) => new()
    {
        Id = record.Id.ToString("D"), ReservationEpoch = checked((ulong)record.Epoch),
        At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Outcome = CalibrationOutcome.Succeeded, CapacityRps = rate
    };

    private static Task<HttpResponseMessage> PostCalibration(Server server, string machine, string? jwt, string? body = null) =>
        server.GetPath($"/api/v1/machines/{machine}/calibrate", jwt, HttpMethod.Post, new StringContent(body ??
            """{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"}}""", Encoding.UTF8, "application/json"));

    private static async Task<WebApplication> StartCalibrationGateway(AgentRegistry registry, CalibrationService calibrations,
        AgentDrainService? drains = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(calibrations);
        if (drains is not null) builder.Services.AddSingleton(drains);
        var app = builder.Build();
        app.MapGrpcService<AgentGatewayService>();
        await app.StartAsync(MachineToken);
        return app;
    }

    private static string GatewayAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class CalibrationClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
}
