using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Agents;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task System_inventory_browser_real_heartbeats_assignments_Admin_boundary_and_accessible_observed_flow()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_SYSTEM_BROWSER") == "true",
            "Set WF_SYSTEM_BROWSER=true and WF_FRONTEND_ROOT after installing dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "system-browser.ts")));
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("system-browser-" + Guid.NewGuid().ToString("N"), "linux");
        hello.MaxTasks = 4;
        hello.Name = "kardia-browser-worker-" + Guid.NewGuid().ToString("N")[..8];
        var session = await registry.RegisterAsync(hello, MachineToken);
        var run = await Run("system-browser-" + Guid.NewGuid().ToString("N") + "/release", Guid.CreateVersion7());
        await using (var tasks = database.DataSource.CreateCommand("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,'build/first',1,$2,5,now()+interval '1 hour'),
                (uuid_v7_run_day($1),$1,'build/second',1,$2,5,now()+interval '1 hour'),
                (uuid_v7_run_day($1),$1,'test/task',1,$2,5,now()+interval '1 hour')
            """))
        {
            tasks.Parameters.AddWithValue(run);
            tasks.Parameters.AddWithValue(session.AgentId);
            await tasks.ExecuteNonQueryAsync(MachineToken);
        }
        await using var server = await SystemServer(app =>
        {
            app.MapPost("/api/fixture/system-work/{busy:bool}", async (bool busy) =>
            {
                await using var update = database.DataSource.CreateCommand("UPDATE task_attempts SET status=$2 WHERE run_id=$1");
                update.Parameters.AddWithValue(run);
                update.Parameters.AddWithValue(busy ? (short)3 : (short)5);
                await update.ExecuteNonQueryAsync(MachineToken);
                if (!await registry.HeartbeatAsync(session, new()
                {
                    At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    SlotsUsed = busy ? 3u : 0u
                }, MachineToken)) throw new InvalidOperationException("System browser fixture lost its agent session.");
                return Results.NoContent();
            }).RequireAuthorization(AuthExtensions.Admin);
        }, reporting: true);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "system-browser.ts"));
        start.Environment["WF_SYSTEM_API"] = server.Api;
        start.Environment["WF_SYSTEM_FIXTURE"] = JsonSerializer.Serialize(new
        {
            workerId = hello.Id, workerName = hello.Name,
            adminToken = server.Token(role: "Admin"), viewerToken = server.Token(role: "Viewer")
        });
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start system browser checks.");
        var stdout = process.StandardOutput.ReadToEndAsync(MachineToken);
        var stderr = process.StandardError.ReadToEndAsync(MachineToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"System browser checks failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }
}
