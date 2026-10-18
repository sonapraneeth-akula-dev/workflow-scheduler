using System.Diagnostics;
using System.Text.Json;
using Wf.ControlPlane.Api.Dashboards;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed partial class DashboardEndpointTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Dashboard_browser_real_Postgres_four_scopes_rankings_filtered_runs_tables_denial_and_accessibility()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_DASHBOARD_BROWSER") == "true",
            "Set WF_DASHBOARD_BROWSER=true and WF_FRONTEND_ROOT with installed browser dependencies.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(System.IO.Path.Combine(frontend, "scripts", "dashboard-browser.ts")));
        var scope = await Registry();
        var empty = await Registry(group: scope.Group);
        var foreign = await Registry(machine: scope.Machine);
        foreach (var (day, duration, status) in new[] { (-89, 10d, (short)3), (-1, 30d, (short)3), (0, 100d, (short)4) })
            await Seed(scope, Today.AddDays(day), status, (short)(status == 3 ? 5 : 7), duration, 150, 128);
        await Seed(foreign, Today, 4, 7, 999999, 999999, 999999);
        await Execute("""
            INSERT INTO node_runs(run_day,run_id,node_key,status,started_at,ended_at)
            SELECT n.run_day,n.run_id,'beta',5,n.started_at,n.started_at+interval '5 seconds'
            FROM node_runs n JOIN runs r ON r.run_day=n.run_day AND r.id=n.run_id
            WHERE r.workflow_id=$1 AND n.node_key='alpha'
            """, scope.Workflow);
        await Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,started_at,ended_at,deadline_at,
                resource_max_cpu_percent,resource_peak_ram_bytes)
            SELECT a.run_day,a.run_id,'beta/task',1,a.agent_id,5,a.started_at,a.started_at+interval '5 seconds',
                a.deadline_at,200,512*1048576
            FROM task_attempts a JOIN runs r ON r.run_day=a.run_day AND r.id=a.run_id
            WHERE r.workflow_id=$1 AND a.task_key='alpha/task'
            """, scope.Workflow);
        await Refresh(scope);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, browser: true, timeProvider: new Clock());
        var viewer = api.Jwt(scope.Group);
        var foreignViewer = api.Jwt(foreign.Group);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(System.IO.Path.Combine("scripts", "dashboard-browser.ts"));
        start.Environment["WF_DASHBOARD_API"] = api.Api;
        start.Environment["WF_DASHBOARD_FIXTURE"] = JsonSerializer.Serialize(new
        {
            workflow = scope.Workflow, workflowId = DashboardQuery.Encode(scope.Workflow),
            nodeId = DashboardQuery.Encode(scope.Workflow + "/alpha"),
            scheduleId = scope.Schedule, machineId = scope.Machine,
            emptyId = DashboardQuery.Encode(empty.Workflow), foreignWorkflow = foreign.Workflow,
            viewerToken = viewer, foreignToken = foreignViewer
        });
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(8));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Dashboard browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        var logs = await stdout + await stderr;
        Assert.DoesNotContain(viewer, logs);
        Assert.DoesNotContain(foreignViewer, logs);
        output.WriteLine(logs);
    }
}
