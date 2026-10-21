using System.Diagnostics;
using System.Text.Json;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task SignOff_browser_signs_revokes_independent_targets_and_exposes_viewer_history_in_runs_and_workflow_history()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_SIGNOFF_BROWSER") == "true",
            "Set WF_SIGNOFF_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "signoff-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        await using var server = await Server.Start(database.DataSource, live: true);
        var viewer = Guid.NewGuid();
        var seed = await SeedRerun(server, viewerGroup: viewer);
        var report = await SeedSignOffReport(server, seed.Snapshot.RunId);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "signoff-browser.ts"));
        start.Environment["WF_SIGNOFF_API"] = server.Api;
        start.Environment["WF_SIGNOFF_FIXTURE"] = JsonSerializer.Serialize(new
        {
            workflowId = seed.Request.WorkflowId, runId = seed.Snapshot.RunId, reportId = report,
            operatorToken = server.Token("", "user", "api.read", groups: [seed.Group], name: "Browser reviewer"),
            viewerToken = server.Token("", "user", "api.read", groups: [viewer], name: "History viewer")
        });
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Sign-off browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        Assert.Equal(12L, await Value("SELECT count(*) FROM run_signoff_events WHERE run_id=$1", seed.Snapshot.RunId));
        Assert.Equal(12L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1 AND action LIKE '%signoff%'", seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }
}
