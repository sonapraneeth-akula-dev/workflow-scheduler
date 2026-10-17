using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Npgsql;
using Wf.Contracts;
using Wf.ControlPlane.Api.Machines;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Live;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task MachineCapacity_manual_overrides_calibrated_for_one_canonical_profile_and_audits_before_after_atomically()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("capacity-" + Guid.NewGuid().ToString("N"), "linux", ("region", "eu"));
        await registry.RegisterAsync(hello, MachineToken);
        var profile = new CapacityProfile("http", "json", "small",
            ImmutableSortedDictionary<string, string>.Empty.Add("connections", "4").Add("threads", "8"));
        var other = new CapacityProfile("http", "json", "large");
        var at = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        await InsertMachineCapacity(Guid.Parse(hello.Id), profile, 1000, CapacitySource.Calibrated, at);
        await InsertMachineCapacity(Guid.Parse(hello.Id), other, 3000, CapacitySource.Calibrated, at);
        await using var server = await Server.Start(database.DataSource, live: true, configure: app => app.MapMachineEndpoints());
        var actor = Guid.NewGuid();
        var jwt = server.Token(role: "Admin", user: actor);
        await using var updates = await MessagePackClient.Connect(server, jwt);
        Assert.Null(await updates.Subscribe("machines", "*"));
        await Task.Delay(350, MachineToken);
        while (updates.Resyncs.Reader.TryRead(out _)) { }
        using (var response = await PatchMachine(server, hello.Id, jwt, """
            {"manualCapacity":{"profile":{"codec":"json","transport":"http","loadSettings":{"threads":"8","connections":"4"},
              "payloadSizeClass":"small"},"capacityRps":1234}}
            """))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new LiveResync("machines", "*"), await updates.ReadResync());
        using (var read = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt))
        {
            var machine = Assert.Single((await Machines(read)).Items);
            Assert.Equal(hello.Pool, machine.Pool);
            Assert.Equal("eu", machine.Labels["region"]);
            Assert.Equal(2, machine.Capacities.Count);
            var manual = Assert.Single(machine.Capacities, item => item.Profile.ProfileKey == profile.ProfileKey);
            Assert.Equal(1234, manual.CapacityRps);
            Assert.Equal("Manual", manual.Source);
            Assert.Null(manual.CalibratedAt);
            var calibrated = Assert.Single(machine.Capacities, item => item.Profile.ProfileKey == other.ProfileKey);
            Assert.Equal(3000, calibrated.CapacityRps);
            Assert.Equal("Calibrated", calibrated.Source);
            Assert.Equal(at, calibrated.CalibratedAt);
        }
        await using var audit = database.DataSource.CreateCommand("""
            SELECT actor,actor_kind,summary::text FROM audit_log WHERE target_id=$1 AND action='machine.update'
            """);
        audit.Parameters.AddWithValue(hello.Id);
        await using (var reader = await audit.ExecuteReaderAsync(MachineToken))
        {
            Assert.True(await reader.ReadAsync(MachineToken));
            Assert.Equal(actor.ToString("D"), reader.GetString(0));
            Assert.Equal("User", reader.GetString(1));
            using var summary = JsonDocument.Parse(reader.GetString(2));
            var before = summary.RootElement.GetProperty("before").GetProperty("manualCapacity");
            Assert.Equal("Calibrated", before.GetProperty("source").GetString());
            Assert.Equal(1000, before.GetProperty("capacityRps").GetInt32());
            var after = summary.RootElement.GetProperty("after").GetProperty("manualCapacity");
            Assert.Equal("Manual", after.GetProperty("source").GetString());
            Assert.Equal(1234, after.GetProperty("capacityRps").GetInt32());
            Assert.Equal(JsonValueKind.Null, after.GetProperty("calibratedAt").ValueKind);
            Assert.False(await reader.ReadAsync(MachineToken));
        }
        await registry.RegisterAsync(hello, MachineToken);
        using var reconnected = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt);
        Assert.Equal(1234, Assert.Single((await Machines(reconnected)).Items).Capacities
            .Single(item => item.Profile.ProfileKey == profile.ProfileKey).CapacityRps);
    }

    [Fact]
    public async Task MachineCapacity_uses_existing_Admin_boundary_and_rejects_bad_or_missing_profiles_without_audit()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("capacity-boundary-" + Guid.NewGuid().ToString("N"), "windows");
        await registry.RegisterAsync(hello, MachineToken);
        await using var server = await MachineServer();
        var profile = new CapacityProfile("http", "json", "small");
        var body = ManualCapacityBody(profile, 100);
        foreach (var role in new[] { "Viewer", "Operator", "Author", "" })
        {
            using var denied = await PatchMachine(server, hello.Id, server.Token(role: role), body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var jwt = server.Token(role: "Admin");
        foreach (var invalid in new[] {
            """{"manualCapacity":{"profile":null,"capacityRps":100}}""",
            """{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":0}}""",
            """{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":100,"source":"Calibrated"}}"""
        })
        {
            using var rejected = await PatchMachine(server, hello.Id, jwt, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using (var missing = await PatchMachine(server, Guid.NewGuid().ToString("D"), jwt, body))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var read = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt))
            Assert.Empty(Assert.Single((await Machines(read)).Items).Capacities);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_id=$1");
        count.Parameters.AddWithValue(hello.Id);
        Assert.Equal(0L, await count.ExecuteScalarAsync(MachineToken));
    }

    [Fact]
    public async Task MachineCapacity_audit_failure_rolls_back_capacity_metadata_and_override_without_notification()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("capacity-rollback-" + Guid.NewGuid().ToString("N"), "linux");
        await registry.RegisterAsync(hello, MachineToken);
        var profile = new CapacityProfile("http", "json", "small");
        await InsertMachineCapacity(Guid.Parse(hello.Id), profile, 500, CapacitySource.Calibrated, DateTimeOffset.UnixEpoch);
        await using var server = await Server.Start(database.DataSource, live: true, configure: app => app.MapMachineEndpoints());
        var jwt = server.Token(role: "Admin");
        await using var updates = await MessagePackClient.Connect(server, jwt);
        Assert.Null(await updates.Subscribe("machines", "*"));
        await Task.Delay(350, MachineToken);
        while (updates.Resyncs.Reader.TryRead(out _)) { }
        var trigger = "reject_capacity_audit_" + Guid.NewGuid().ToString("N");
        await using (var create = database.DataSource.CreateCommand($$"""
            CREATE FUNCTION {{trigger}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private-capacity-audit-failure'; END $$;
            CREATE TRIGGER {{trigger}} BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION {{trigger}}()
            """))
            await create.ExecuteNonQueryAsync(MachineToken);
        try
        {
            using var response = await PatchMachine(server, hello.Id, jwt,
                """{"pool":"must-roll-back","manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":999}}""");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("private-capacity-audit-failure", await response.Content.ReadAsStringAsync(MachineToken));
            using var read = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt);
            var machine = Assert.Single((await Machines(read)).Items);
            Assert.Equal(hello.Pool, machine.Pool);
            var capacity = Assert.Single(machine.Capacities);
            Assert.Equal(500, capacity.CapacityRps);
            Assert.Equal("Calibrated", capacity.Source);
            await Task.Delay(350, MachineToken);
            Assert.False(updates.Resyncs.Reader.TryPeek(out _));
        }
        finally
        {
            await using var drop = database.DataSource.CreateCommand($"DROP TRIGGER {trigger} ON audit_log; DROP FUNCTION {trigger}()");
            await drop.ExecuteNonQueryAsync(MachineToken);
        }
    }

    [Fact]
    public async Task MachineCapacity_manual_changes_do_not_interrupt_the_current_physical_reservation()
    {
        await using var scenario = await QueueScenario();
        await using var server = await MachineServer();
        var jwt = server.Token(role: "Admin");
        MachineResponse before;
        using (var read = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, jwt))
            before = Assert.Single((await Machines(read)).Items);
        using (var response = await PatchMachine(server, before.Id.ToString("D"), jwt,
            ManualCapacityBody(new("http", "json", "small"), int.MaxValue)))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var afterResponse = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, jwt);
        var after = Assert.Single((await Machines(afterResponse)).Items);
        Assert.Equal(before.CurrentReservation, after.CurrentReservation);
        Assert.Equal(before.SlotsUsed, after.SlotsUsed);
        Assert.Equal(int.MaxValue, Assert.Single(after.Capacities).CapacityRps);
    }

    private async Task InsertMachineCapacity(Guid agent, CapacityProfile profile, int rps, CapacitySource source, DateTimeOffset? at)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO agent_capacities(agent_id,profile_key,capacity_rps,source,calibrated_at) VALUES($1,$2,$3,$4,$5)
            """);
        command.Parameters.AddWithValue(agent);
        command.Parameters.AddWithValue(profile.ProfileKey);
        command.Parameters.AddWithValue(rps);
        command.Parameters.AddWithValue((short)source);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, at is null ? DBNull.Value : at.Value);
        await command.ExecuteNonQueryAsync(MachineToken);
    }

    [Fact]
    public async Task MachineCapacity_parallel_inventory_reads_keep_pooled_readers_isolated()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("capacity-parallel-" + Guid.NewGuid().ToString("N"), "linux");
        var session = await registry.RegisterAsync(hello, MachineToken);
        await using var server = await Server.Start(database.DataSource, live: true, configure: app => app.MapMachineEndpoints());
        var jwt = server.Token(role: "Admin");
        for (var round = 0; round < 6; round++)
        {
            if (round == 3)
                await InsertMachineCapacity(session.AgentId, new("http", "json", "small"), 1234, CapacitySource.Manual, null);
            Assert.True(await registry.HeartbeatAsync(session, new()
            {
                At = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), CpuPercent = 37
            }, MachineToken));
            await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            {
                using var response = await server.GetPath("/api/v1/machines?pool=" + hello.Pool, jwt);
                var machine = Assert.Single((await Machines(response)).Items);
                Assert.Equal(37, machine.CpuPercent);
                Assert.Equal(round < 3 ? 0 : 1, machine.Capacities.Count);
                if (round >= 3) Assert.Equal(1234, Assert.Single(machine.Capacities).CapacityRps);
            }));
        }
    }

    private static string ManualCapacityBody(CapacityProfile profile, int rps) =>
        """{"manualCapacity":{"profile":""" + profile.ProfileKey + ""","capacityRps":""" +
        rps.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
}
