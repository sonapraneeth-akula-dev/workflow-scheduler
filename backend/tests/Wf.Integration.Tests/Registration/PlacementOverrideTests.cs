using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class TriggerRunTests
{
    [Fact]
    public async Task Single_machine_actual_wf_run_CLI_accepts_real_API_and_recovery_persists_override()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        using var agents = new AgentRegistry(database.DataSource, server.Clock);
        var machine = "single-" + Guid.NewGuid().ToString("N");
        await agents.RegisterAsync(new()
        {
            Id = Guid.NewGuid().ToString("D"), Name = machine, Os = "windows", Arch = "x64",
            Pool = "default", Cores = 4, RamMb = 8192, MaxTasks = 2, Version = "1", ProtocolVersion = 1
        }, Token);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { typeof(Wf.Cli.Commands.RunCommand).Assembly.Location, "run", request.WorkflowId!,
            "--single-machine", "--machine", machine, "--engine", server.Api })
            start.ArgumentList.Add(arg);
        start.Environment["WF_ACCESS_TOKEN"] = Operator(server, group);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.True(process.ExitCode == 0, await stderr);
            var runId = Guid.Parse((await stdout).Trim());
            var initial = (await Payload(runId)).GetProperty("InitialState");
            Assert.Equal(machine, initial.GetProperty("Placement").GetProperty("Machine").GetString());
            using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"),
                new Uri("http://localhost:5263")), NullLogger<LeaseManager>.Instance);
            Assert.True(await leader.TryAcquireOrRenewAsync(Token));
            try
            {
                var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
                using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
                await recovery.RecoverAsync(Token);
                var state = (await store.LoadAsync(runId, Token)).Snapshot;
                Assert.Equal(new(true, machine), state.Placement);
                var persisted = JsonSerializer.Deserialize(Assert.IsType<string>(
                    await Scalar("SELECT placement::text FROM runs WHERE id=$1", runId)), RunJsonContext.Default.RunPlacement);
                Assert.Equal(state.Placement, persisted);
                using var read = await server.Send(HttpMethod.Get, $"/api/v1/runs/{runId:D}", null!, Operator(server, group));
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal(machine, (await Json(read)).GetProperty("placement").GetProperty("machine").GetString());
            }
            finally { await leader.StopAsync(Token); }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("mixed-os")]
    [InlineData("draining")]
    [InlineData("unhealthy")]
    [InlineData("expired")]
    [InlineData("unknown")]
    [InlineData("reservation-count")]
    [InlineData("reserved")]
    public async Task Single_machine_trigger_returns_400_with_conflicting_nodes_and_picker_excludes_ineligible(string invalid)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var machine = "eligible-" + Guid.NewGuid().ToString("N");
        using var agents = new AgentRegistry(database.DataSource, server.Clock);
        var session = await agents.RegisterAsync(new()
        {
            Id = Guid.NewGuid().ToString("D"), Name = machine, Os = "windows", Arch = "x64", Pool = machine,
            Cores = 4, RamMb = 8192, MaxTasks = 2, Version = "1", ProtocolVersion = 1
        }, Token);
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Nodes = [
                new("windows", [], [new("task", placement: new(new(os: "windows", pool: machine)))]),
                new("conflict", [], [new("task", placement: new(new(
                    os: invalid == "mixed-os" ? "linux" : "windows", pool: machine)))],
                    placement: invalid == "reservation-count" ? new(new(count: 2)) : null)]
        };
        // Registration checks the static inventory for ordinary selectors before accepting the immutable manifest.
        if (invalid == "mixed-os")
            await agents.RegisterAsync(new()
            {
                Id = Guid.NewGuid().ToString("D"), Name = machine + "-linux", Os = "linux", Arch = "x64",
                Pool = machine, Cores = 4, RamMb = 8192, MaxTasks = 2, Version = "1", ProtocolVersion = 1
            }, Token);
        if (invalid == "reservation-count")
            await agents.RegisterAsync(new()
            {
                Id = Guid.NewGuid().ToString("D"), Name = machine + "-extra", Os = "windows", Arch = "x64",
                Pool = machine, Cores = 4, RamMb = 8192, MaxTasks = 2, Version = "1", ProtocolVersion = 1
            }, Token);
        request = request with { Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest) };
        var group = Guid.NewGuid();
        var version = await Register(server, request, group);
        if (invalid is "draining" or "unhealthy")
            await Execute("UPDATE agents SET status=$2 WHERE id=$1", session.AgentId, (short)(invalid == "draining" ? 2 : 1));
        if (invalid == "expired")
            await Execute("UPDATE agents SET last_heartbeat_at=$2 WHERE id=$1", session.AgentId,
                server.Clock.Now - AgentRegistry.HeartbeatTimeout);
        if (invalid == "reserved")
        {
            var owner = new RunState(Guid.CreateVersion7(), manifest.Nodes, manifest.Metadata, Assert.Fail);
            await new RunStore(database.DataSource, NullLogger<RunStore>.Instance).CreateAsync(owner,
                request.WorkflowId!, version, DateTimeOffset.UtcNow.AddHours(1), Token);
            await Execute("""
                INSERT INTO reservations(id,run_day,run_id,node_key,agent_id,selector,created_at,renewed_at,expires_at)
                VALUES($1,uuid_v7_run_day($2),$2,'windows',$3,'{}', $4,$4,$4+interval '1 minute')
                """, Guid.CreateVersion7(), owner.Snapshot.RunId, session.AgentId, server.Clock.Now);
        }
        var trigger = new TriggerRunRequest(request.WorkflowId!) { Placement = new(true, invalid == "unknown" ? "missing" : machine) };
        using var response = await Trigger(server, trigger, Operator(server, group));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await Json(response)).GetProperty("errors");
        Assert.Contains("conflict", errors.ToString());
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        using var options = await server.Send(HttpMethod.Post, "/api/v1/runs/placement-options",
            JsonContent.Create(new TriggerRunRequest(request.WorkflowId!), RunJsonContext.Default.TriggerRunRequest), Operator(server, group));
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var candidates = (await Json(options)).GetProperty("items").EnumerateArray().ToArray();
        if (invalid == "unknown") Assert.Contains(candidates, candidate => candidate.GetProperty("name").GetString() == machine);
        else Assert.Empty(candidates);
        Assert.Empty(server.Failures);
    }
}

public sealed partial class RegistrationTests
{
    [Fact]
    public async Task Single_machine_rerun_inherits_or_explicitly_changes_override_and_fingerprints_changes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server, placement: new(true));
        using var agents = new AgentRegistry(database.DataSource, server.Clock);
        var machine = "rerun-" + Guid.NewGuid().ToString("N");
        await agents.RegisterAsync(new()
        {
            Id = Guid.NewGuid().ToString("D"), Name = machine, Os = "windows", Arch = "x64",
            Pool = "default", Cores = 4, RamMb = 8192, MaxTasks = 2, Version = "1", ProtocolVersion = 1
        }, token);
        var bearer = RerunOperator(server, seed.Group);
        var key = Guid.NewGuid().ToString("D");
        var inherited = await RerunAccepted(server, seed.Snapshot.RunId, new(), bearer, key);
        var disabled = await RerunAccepted(server, seed.Snapshot.RunId, new(Placement: new(false)), bearer);
        var named = await RerunAccepted(server, seed.Snapshot.RunId, new(Placement: new(true, machine)), bearer);
        using var conflict = await RerunSend(server, seed.Snapshot.RunId, new(Placement: new(false)), bearer, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        foreach (var (id, expected) in new[] { (inherited, new RunPlacement(true)), (disabled, new RunPlacement(false)),
            (named, new RunPlacement(true, machine)) })
        {
            using var pending = await server.Send(HttpMethod.Get, $"/api/v1/runs/{id:D}", null!, bearer);
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            Assert.Equal(expected.SingleMachine, (await Json(pending)).GetProperty("placement").GetProperty("singleMachine").GetBoolean());
        }
        using var options = await server.Send(HttpMethod.Post, $"/api/v1/runs/{seed.Snapshot.RunId:D}/placement-options",
            JsonContent.Create(new RerunRunRequest(), RunJsonContext.Default.RerunRunRequest), bearer);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.Contains((await Json(options)).GetProperty("items").EnumerateArray(),
            candidate => candidate.GetProperty("name").GetString() == machine);
        using var leader = new LeaseManager(database.DataSource, new(Guid.NewGuid().ToString("N"),
            new Uri("http://localhost:5264")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(token));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(token);
            Assert.True((await store.LoadAsync(inherited, token)).Snapshot.Placement!.SingleMachine);
            Assert.False((await store.LoadAsync(disabled, token)).Snapshot.Placement!.SingleMachine);
            Assert.Equal(machine, (await store.LoadAsync(named, token)).Snapshot.Placement!.Machine);
            Assert.Null((await store.LoadAsync(named, token)).Snapshot.PlacementAgentId);
        }
        finally { await leader.StopAsync(token); }
        Assert.Empty(server.Failures);
    }
}
