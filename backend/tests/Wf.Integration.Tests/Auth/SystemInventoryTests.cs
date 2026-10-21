using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api.Admin;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Hosting;
using Wf.ControlPlane.Live;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task System_inventory_actual_API_process_registers_traffic_and_crash_expires_without_touching_other_instances()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        using var enginePort = new TcpListener(IPAddress.Loopback, 0);
        using var apiPort = new TcpListener(IPAddress.Loopback, 0);
        enginePort.Start();
        apiPort.Start();
        var engine = ((IPEndPoint)enginePort.LocalEndpoint).Port;
        var api = ((IPEndPoint)apiPort.LocalEndpoint).Port;
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "system-process-" + Guid.NewGuid().ToString("N")));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Migrator).Assembly.Location);
        start.ArgumentList.Add("--dev");
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Workflow"] = database.ConnectionString;
        start.Environment["Engine__Enabled"] = "false";
        start.Environment["Engine__Port"] = engine.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Api__Port"] = api.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Storage__Root"] = root;
        start.Environment["Storage__UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        start.Environment["Runtime__Name"] = "kardia-test-api-" + Guid.NewGuid().ToString("N");
        enginePort.Stop();
        apiPort.Stop();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated API process.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{api}"), Timeout = TimeSpan.FromSeconds(3)
            };
            SystemInstanceResponse? observed = null;
            while (observed is null || observed.HttpRequests == 0)
            {
                if (process.HasExited)
                    Assert.Fail($"Isolated API exited {process.ExitCode}.\n{await stdout}\n{await stderr}");
                try
                {
                    using var response = await client.GetAsync("/api/v1/admin/system", token);
                    var snapshot = await SystemSnapshot(response);
                    var id = Assert.Single(response.Headers.GetValues("X-Kardia-Instance"));
                    observed = snapshot.Instances.SingleOrDefault(row => row.Id == id);
                }
                catch (HttpRequestException) when (!process.HasExited) { }
                if (observed is null || observed.HttpRequests == 0) await Task.Delay(100, token);
            }
            Assert.Equal("Api", observed.Role);
            Assert.Equal("Healthy", observed.Status);
            Assert.False(observed.IsLeader);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token);
            await using var observer = await SystemServer();
            SystemInstanceResponse stale;
            do
            {
                await Task.Delay(100, token);
                using var response = await observer.GetPath("/api/v1/admin/system", observer.Token(role: "Admin"));
                stale = Assert.Single((await SystemSnapshot(response)).Instances, row => row.Id == observed.Id);
            } while (stale.Status == "Healthy");
            Assert.Equal("Unhealthy", stale.Status);
            Assert.False(stale.IsLeader);
            Assert.Equal(observed.LastHeartbeatAt, stale.LastHeartbeatAt);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task System_inventory_requires_global_Admin_on_every_poll_and_rejects_unknown_filters()
    {
        await using var server = await SystemServer();
        foreach (var role in new[] { "", "Viewer", "Operator", "Author", "Agent", "Registrar", "admin" })
        {
            using var denied = await server.GetPath("/api/v1/admin/system", server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var anonymous = await server.GetPath("/api/v1/admin/system", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var unsigned = await server.GetPath("/api/v1/admin/system", server.Token(role: "Admin", unsigned: true)))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var group = Guid.NewGuid();
        await Seed(RegistryRole.Admin, group, true);
        using (var teamAdmin = await server.GetPath("/api/v1/admin/system", server.Token(group: group)))
            Assert.Equal(HttpStatusCode.Forbidden, teamAdmin.StatusCode);
        using (var allowed = await server.GetPath("/api/v1/admin/system", server.Token(role: "Admin")))
            await SystemSnapshot(allowed);
        using (var revoked = await server.GetPath("/api/v1/admin/system", server.Token(role: "Viewer")))
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var invalid = await server.GetPath("/api/v1/admin/system?includeSecrets=true", server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task System_inventory_uses_database_heartbeat_and_current_lease_not_desired_replicas_or_cached_leadership()
    {
        await using var server = await SystemServer();
        var reporter = server.App.Services.GetRequiredService<RuntimeInstanceReporter>();
        var instance = server.App.Services.GetRequiredService<RuntimeInstance>();
        await reporter.ReportAsync(MachineToken);
        await using var leader = database.DataSource.CreateCommand("""
            INSERT INTO leases(name,holder,leader_hint,epoch,expires_at)
            VALUES('engine',$1,'http://localhost:5000/',1,clock_timestamp()+interval '1 minute')
            ON CONFLICT(name) DO UPDATE SET holder=EXCLUDED.holder,expires_at=EXCLUDED.expires_at
            """);
        leader.Parameters.AddWithValue(instance.Id);
        await leader.ExecuteNonQueryAsync(MachineToken);
        var jwt = server.Token(role: "Admin");
        async Task<SystemInstanceResponse> Current()
        {
            using var response = await server.GetPath("/api/v1/admin/system", jwt);
            return Assert.Single((await SystemSnapshot(response)).Instances, row => row.Id == instance.Id);
        }
        var active = await Current();
        Assert.Equal("Healthy", active.Status);
        Assert.True(active.IsLeader);
        Assert.Equal("ApiController", active.Role);
        Assert.Equal("kardia-lab-vm-1", active.NodeName);
        Assert.Equal("kardia-controller-0", active.PodName);
        Assert.Equal("kardia", active.Namespace);
        await using var expire = database.DataSource.CreateCommand(
            "UPDATE leases SET expires_at=clock_timestamp()-interval '1 second' WHERE name='engine'");
        await expire.ExecuteNonQueryAsync(MachineToken);
        Assert.False((await Current()).IsLeader);
        await using var stale = database.DataSource.CreateCommand(
            "UPDATE runtime_instances SET last_heartbeat_at=clock_timestamp()-interval '16 seconds' WHERE id=$1");
        stale.Parameters.AddWithValue(instance.Id);
        await stale.ExecuteNonQueryAsync(MachineToken);
        Assert.Equal("Unhealthy", (await Current()).Status);
        await reporter.ReportAsync(MachineToken);
        var drain = server.App.Services.GetRequiredService<DrainService>();
        await drain.StoppingAsync(MachineToken);
        await reporter.ReportAsync(MachineToken);
        Assert.Equal("Draining", (await Current()).Status);
        await reporter.StopAsync(MachineToken);
        Assert.Equal("Stopped", (await Current()).Status);
    }

    [Fact]
    public async Task System_inventory_counts_assigned_tasks_parent_DAG_nodes_runs_and_workflows_per_actual_agent()
    {
        await using var server = await SystemServer();
        var jwt = server.Token(role: "Admin");
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("system-" + Guid.NewGuid().ToString("N"), "linux");
        await registry.RegisterAsync(hello, MachineToken);
        var run = await Run("system-" + Guid.NewGuid().ToString("N") + "/release", Guid.CreateVersion7());
        var another = await Run("system-" + Guid.NewGuid().ToString("N") + "/release", Guid.CreateVersion7());
        using var baselineResponse = await server.GetPath("/api/v1/admin/system", jwt);
        var baseline = await SystemSnapshot(baselineResponse);
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,'build/task-a',1,$3,2,now()+interval '1 hour'),
                (uuid_v7_run_day($1),$1,'build/task-b',1,$3,3,now()+interval '1 hour'),
                (uuid_v7_run_day($1),$1,'test/task',1,$3,3,now()+interval '1 hour'),
                (uuid_v7_run_day($2),$2,'build/task',1,$3,3,now()+interval '1 hour'),
                (uuid_v7_run_day($1),$1,'done/task',1,$3,5,now()+interval '1 hour'),
                (uuid_v7_run_day($1),$1,'retry/task',1,$3,4,now()+interval '1 hour')
            """);
        seed.Parameters.AddWithValue(run);
        seed.Parameters.AddWithValue(another);
        seed.Parameters.AddWithValue(Guid.Parse(hello.Id));
        await seed.ExecuteNonQueryAsync(MachineToken);
        await using var stale = database.DataSource.CreateCommand(
            "UPDATE agents SET last_heartbeat_at=now()-interval '1 minute' WHERE id=$1");
        stale.Parameters.AddWithValue(Guid.Parse(hello.Id));
        await stale.ExecuteNonQueryAsync(MachineToken);
        using var response = await server.GetPath("/api/v1/admin/system", jwt);
        var snapshot = await SystemSnapshot(response);
        var worker = Assert.Single(snapshot.Workers, row => row.Id == Guid.Parse(hello.Id));
        Assert.Equal("Unhealthy", worker.Status);
        Assert.Equal(4, worker.AssignedTasks);
        Assert.Equal(3, worker.RunningTasks);
        Assert.Equal(3, worker.ActiveDagNodes);
        Assert.Equal(2, worker.ActiveRuns);
        Assert.Equal(2, worker.ActiveWorkflows);
        Assert.Equal(baseline.Totals.AssignedTasks + 4, snapshot.Totals.AssignedTasks);
        Assert.Equal(baseline.Totals.RunningTasks + 3, snapshot.Totals.RunningTasks);
        Assert.Equal(baseline.Totals.ActiveDagNodes + 3, snapshot.Totals.ActiveDagNodes);
        var body = await response.Content.ReadAsStringAsync(MachineToken);
        foreach (var secret in new[] { "sessionId", "reservationEpoch", "leaderHint", "variables", "access_token" })
            Assert.DoesNotContain(secret, body);
    }

    [Fact]
    public async Task System_reporter_records_actual_requests_live_subscriptions_and_successful_sends_and_cleans_old_processes()
    {
        await using var server = await SystemServer();
        var reporter = server.App.Services.GetRequiredService<RuntimeInstanceReporter>();
        var instance = server.App.Services.GetRequiredService<RuntimeInstance>();
        var live = server.App.Services.GetRequiredService<LiveSubscriptions>();
        var activity = server.App.Services.GetRequiredService<RuntimeActivity>();
        using (var health = await server.GetPath("/healthz", null))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, activity.HttpRequests);
        using (var request = await server.GetPath("/api/v1/admin/system", server.Token(role: "Admin")))
            Assert.Equal(instance.Id, Assert.Single(request.Headers.GetValues("X-Kardia-Instance")));
        Assert.Equal(1, activity.HttpRequests);
        await using var client = await server.Connect(server.Token(role: "Admin"));
        Assert.Equal(JsonValueKind.Null, (await client.Subscribe("machines", "*")).ValueKind);
        await live.PublishAsync("machines", "*", "SystemTest", new { value = 1 }, MachineToken);
        await reporter.ReportAsync(MachineToken);
        using var response = await server.GetPath("/api/v1/admin/system", server.Token(role: "Admin"));
        var row = Assert.Single((await SystemSnapshot(response)).Instances, row => row.Id == instance.Id);
        Assert.Equal(1, row.HttpRequests);
        Assert.Equal(1, row.LiveConnections);
        Assert.Equal(1, row.LiveSubscriptions);
        Assert.Equal(1, row.LiveDeliveries);
        await using var old = database.DataSource.CreateCommand(
            "UPDATE runtime_instances SET last_heartbeat_at=now()-interval '2 days' WHERE id=$1");
        old.Parameters.AddWithValue(instance.Id);
        await old.ExecuteNonQueryAsync(MachineToken);
        // A replacement process has a different identity, even on the same VM/pod.
        var replacement = instance with { Id = Guid.NewGuid().ToString("N") };
        var replacementReporter = new RuntimeInstanceReporter(database.DataSource, replacement, activity, live,
            server.App.Services.GetRequiredService<DrainService>(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RuntimeInstanceReporter>.Instance);
        await replacementReporter.ReportAsync(MachineToken);
        using var refreshed = await server.GetPath("/api/v1/admin/system", server.Token(role: "Admin"));
        var rows = (await SystemSnapshot(refreshed)).Instances;
        Assert.DoesNotContain(rows, item => item.Id == instance.Id);
        Assert.Contains(rows, item => item.Id == replacement.Id);
    }

    [Fact]
    public async Task System_inventory_storage_failure_is_explicit_and_does_not_echo_database_details()
    {
        await using var server = await SystemServer();
        await using var hide = database.DataSource.CreateCommand("ALTER TABLE runtime_instances RENAME TO hidden_runtime_instances");
        await hide.ExecuteNonQueryAsync(MachineToken);
        try
        {
            using var response = await server.GetPath("/api/v1/admin/system", server.Token(role: "Admin"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var text = await response.Content.ReadAsStringAsync(MachineToken);
            Assert.Contains("System inventory unavailable", text);
            Assert.DoesNotContain("runtime_instances", text);
        }
        finally
        {
            await using var restore = database.DataSource.CreateCommand("ALTER TABLE hidden_runtime_instances RENAME TO runtime_instances");
            await restore.ExecuteNonQueryAsync(MachineToken);
        }
    }

    private Task<Server> SystemServer() => Server.Start(database.DataSource, configure: app =>
    {
        app.UseRuntimeActivity();
        app.MapSystemEndpoints();
        app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous();
    }, configureServices: services =>
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Drain:PropagationDelay"] = "00:00:00", ["Runtime:Name"] = "kardia-controller-0",
            ["Runtime:NodeName"] = "kardia-lab-vm-1", ["Runtime:PodName"] = "kardia-controller-0",
            ["Runtime:Namespace"] = "kardia"
        }).Build();
        services.AddWorkflowDrain(configuration, true);
        services.AddSingleton(RuntimeInstance.Read(configuration, true));
        services.AddSingleton<RuntimeActivity>();
        services.AddSingleton<RuntimeInstanceReporter>();
    });

    private static async Task<SystemSnapshotResponse> SystemSnapshot(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        return (await response.Content.ReadFromJsonAsync(SystemJsonContext.Default.SystemSnapshotResponse, MachineToken))!;
    }
}
