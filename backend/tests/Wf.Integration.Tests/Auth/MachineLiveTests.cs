using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.ControlPlane.Api.Machines;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Live;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task Machines_live_feed_invalidates_only_Admin_inventory_after_commit_and_real_registry_heartbeats()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("live-" + Guid.NewGuid().ToString("N"), "windows");
        var session = await registry.RegisterAsync(hello, MachineToken);
        await using var server = await Server.Start(database.DataSource, live: true, configure: app => app.MapMachineEndpoints());
        await using var admin = await MessagePackClient.Connect(server, server.Token(role: "Admin"));
        await using var viewer = await MessagePackClient.Connect(server, server.Token(role: "Viewer"));
        Assert.Null(await admin.Subscribe("machines", "*"));
        Assert.Equal(403, (await viewer.Subscribe("machines", "*"))!.Status);
        Assert.Equal(403, (await admin.Subscribe("machines", hello.Id))!.Status);
        Assert.Null(await admin.Subscribe("machines", "*"));
        await Task.Delay(350, MachineToken);
        while (admin.Resyncs.Reader.TryRead(out _)) { }
        await using (var connection = await database.DataSource.OpenConnectionAsync(MachineToken))
        await using (var transaction = await connection.BeginTransactionAsync(MachineToken))
        {
            await using var update = new NpgsqlCommand("UPDATE agents SET pool='uncommitted' WHERE id=$1", connection, transaction);
            update.Parameters.AddWithValue(session.AgentId);
            await update.ExecuteNonQueryAsync(MachineToken);
            await Task.Delay(350, MachineToken);
            Assert.False(admin.Resyncs.Reader.TryPeek(out _));
            await transaction.RollbackAsync(MachineToken);
        }
        await Task.Delay(350, MachineToken);
        Assert.False(admin.Resyncs.Reader.TryPeek(out _));
        using (var mutation = await PatchMachine(server, hello.Id, server.Token(role: "Admin"), """{"pool":"committed-live"}"""))
            Assert.Equal(HttpStatusCode.NoContent, mutation.StatusCode);
        Assert.Equal(new LiveResync("machines", "*"), await admin.ReadResync());
        Assert.True(await registry.HeartbeatAsync(session, new()
        {
            At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            CpuPercent = 37, RamUsedMb = 1024, DiskFreeMb = 4096, SlotsUsed = 1
        }, MachineToken));
        Assert.Equal(new LiveResync("machines", "*"), await admin.ReadResync());
        using (var snapshot = await server.GetPath("/api/v1/machines?pool=committed-live", server.Token(role: "Admin")))
        {
            var row = Assert.Single((await Machines(snapshot)).Items);
            Assert.Equal(37, row.CpuPercent);
            Assert.Equal(1, row.SlotsUsed);
            Assert.Equal("committed-live", row.Pool);
        }
        await using (var unchanged = database.DataSource.CreateCommand("UPDATE agents SET pool=pool WHERE id=$1"))
        {
            unchanged.Parameters.AddWithValue(session.AgentId);
            await unchanged.ExecuteNonQueryAsync(MachineToken);
        }
        await Task.Delay(350, MachineToken);
        Assert.False(admin.Resyncs.Reader.TryPeek(out _));
        Assert.False(viewer.Resyncs.Reader.TryPeek(out _));
        Assert.False(admin.Runs.Reader.TryPeek(out _));
        Assert.False(admin.Lists.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task Machines_live_reservation_release_pending_and_physical_settlement_invalidate_inventory()
    {
        await using var scenario = await QueueScenario();
        await using var server = await Server.Start(database.DataSource, live: true, configure: app => app.MapMachineEndpoints());
        await using var admin = await MessagePackClient.Connect(server, server.Token(role: "Admin"));
        Assert.Null(await admin.Subscribe("machines", "*"));
        await Task.Delay(350, MachineToken);
        while (admin.Resyncs.Reader.TryRead(out _)) { }
        using var leases = new ReservationLeaseService(database.DataSource, scenario.Store, TimeProvider.System,
            NullLogger<ReservationLeaseService>.Instance);
        await leases.ReleaseAsync(scenario.Owner.Snapshot.RunId, "work", "Machine live release test", MachineToken);
        Assert.Equal(new LiveResync("machines", "*"), await admin.ReadResync());
        using (var pending = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, server.Token(role: "Admin")))
            Assert.True(Assert.Single((await Machines(pending)).Items).CurrentReservation!.ReleasePending);
        await scenario.ReleaseOwner();
        Assert.Equal(new LiveResync("machines", "*"), await admin.ReadResync());
        using var released = await server.GetPath("/api/v1/machines?pool=" + scenario.Pool, server.Token(role: "Admin"));
        Assert.Null(Assert.Single((await Machines(released)).Items).CurrentReservation);
    }

    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Machines_browser_real_Postgres_registry_heartbeats_reservation_audited_edit_and_Viewer_denial()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_MACHINE_BROWSER") == "true",
            "Set WF_MACHINE_BROWSER=true and WF_FRONTEND_ROOT after installing dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "machines-browser.ts")));
        await using var scenario = await QueueScenario();
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello(scenario.Pool, "windows", ("region", "eu"));
        hello.MaxTasks = 4;
        var session = await registry.RegisterAsync(hello, MachineToken);
        using var leases = new ReservationLeaseService(database.DataSource, scenario.Store, TimeProvider.System,
            NullLogger<ReservationLeaseService>.Instance);
        await leases.ReleaseAsync(scenario.Owner.Snapshot.RunId, "work", "Browser physical release pending", MachineToken);
        await using var server = await Server.Start(database.DataSource, live: true, configure: app =>
        {
            app.MapMachineEndpoints();
            app.MapPost("/api/fixture/machine-heartbeat/{busy:bool}", async (bool busy) =>
            {
                if (!await registry.HeartbeatAsync(session, new()
                {
                    At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    CpuPercent = busy ? 37 : 0, RamUsedMb = 1024, DiskFreeMb = 4096, SlotsUsed = busy ? 1u : 0u
                }, MachineToken)) throw new InvalidOperationException("Fixture lost its registered agent session.");
                return Results.NoContent();
            }).RequireAuthorization(AuthExtensions.Admin);
            app.MapPost("/api/fixture/machine-capacity/{calibrated:bool}", async (bool calibrated) =>
            {
                await using var clear = database.DataSource.CreateCommand("DELETE FROM agent_capacities WHERE agent_id=$1");
                clear.Parameters.AddWithValue(session.AgentId);
                await clear.ExecuteNonQueryAsync(MachineToken);
                if (calibrated)
                {
                    var at = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
                    await InsertMachineCapacity(session.AgentId, new("http", "json", "small"), 1000, CapacitySource.Calibrated, at);
                    await InsertMachineCapacity(session.AgentId, new("http", "json", "large"), 3000, CapacitySource.Calibrated, at);
                }
                return Results.NoContent();
            }).RequireAuthorization(AuthExtensions.Admin);
        });
        var actor = Guid.NewGuid();
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "machines-browser.ts"));
        start.Environment["WF_MACHINE_API"] = server.Api;
        start.Environment["WF_MACHINE_FIXTURE"] = JsonSerializer.Serialize(new
        {
            pool = scenario.Pool, machineId = hello.Id, machineName = hello.Name,
            reservedRunId = scenario.Owner.Snapshot.RunId, adminToken = server.Token(role: "Admin", user: actor),
            viewerToken = server.Token(role: "Viewer")
        });
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(MachineToken);
        var stderr = process.StandardError.ReadToEndAsync(MachineToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Machine browsers failed.\n{await stdout}\n{await stderr}");
            await using var audit = database.DataSource.CreateCommand("""
                SELECT count(*) FROM audit_log WHERE actor=$1 AND target_kind='Machine' AND target_id=$2
                  AND action='machine.update' AND summary->'after'->'labels'->>'region'='us <script>'
                  AND NOT (summary->'after' ? 'manualCapacity')
                """);
            audit.Parameters.AddWithValue(actor.ToString("D"));
            audit.Parameters.AddWithValue(hello.Id);
            Assert.Equal(3L, await audit.ExecuteScalarAsync(MachineToken));
            await using var capacities = database.DataSource.CreateCommand("""
                SELECT count(*) FROM audit_log WHERE actor=$1 AND target_kind='Machine' AND target_id=$2
                  AND action='machine.update'
                  AND summary->'before'->'manualCapacity'->>'source'='Calibrated'
                  AND summary->'after'->'manualCapacity'->>'source'='Manual'
                  AND summary->'after'->'manualCapacity'->>'capacityRps'='1234'
                """);
            capacities.Parameters.AddWithValue(actor.ToString("D"));
            capacities.Parameters.AddWithValue(hello.Id);
            Assert.Equal(3L, await capacities.ExecuteScalarAsync(MachineToken));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }
}
