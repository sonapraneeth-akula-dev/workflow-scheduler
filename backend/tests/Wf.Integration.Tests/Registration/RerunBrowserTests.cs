using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Rerun_browser_confirms_Operator_retries_one_command_and_links_actual_reused_lineage_with_real_jwt()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_RERUN_BROWSER") == "true",
            "Set WF_RERUN_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "rerun-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        await using var server = await Server.Start(database.DataSource, live: true);
        var viewer = Guid.NewGuid();
        var seed = await SeedRerun(server, viewerGroup: viewer);
        var manifest = seed.Request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        using var newer = await server.Post(seed.Request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest with
            {
                Version = "2.0.0",
                Variables = [new("environment", VariableType.String, true, JsonSerializer.SerializeToElement("new-default"))]
            }, WorkflowManifestJsonContext.Default.WorkflowManifest)
        });
        Assert.Equal(HttpStatusCode.Created, newer.StatusCode);
        var latest = (await Json(newer)).GetProperty("workflowVersion").GetGuid();
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "rerun-browser.ts"));
        start.Environment["WF_RERUN_API"] = server.Api;
        start.Environment["WF_RERUN_FIXTURE"] = JsonSerializer.Serialize(new
        {
            workflowId = seed.Request.WorkflowId, source = seed.Snapshot.RunId, version = seed.Version,
            operatorToken = RerunOperator(server, seed.Group),
            viewerToken = server.Token("", "user", "api.read", groups: [viewer])
        });
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Rerun browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        Assert.Equal(3L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1", seed.Snapshot.RunId.ToString("D")));
        Assert.Equal(3L, await Value("SELECT count(*) FROM audit_log WHERE action='run.rerun' AND summary->>'parentRunId'=$1",
            seed.Snapshot.RunId.ToString("D")));
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5261")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(TestContext.Current.CancellationToken));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(TestContext.Current.CancellationToken);
            var children = new List<Guid>();
            await using (var query = database.DataSource.CreateCommand("SELECT id FROM runs WHERE parent_run_id=$1"))
            {
                query.Parameters.AddWithValue(seed.Snapshot.RunId);
                await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken)) children.Add(reader.GetGuid(0));
            }
            Assert.Equal(3, children.Count);
            foreach (var child in children)
            {
                var state = await store.LoadAsync(child, TestContext.Current.CancellationToken);
                Assert.Equal("ppe", state.Snapshot.Variables["environment"].GetString());
                Assert.Equal("rerun-original", state.Snapshot.Metadata.Values["area"]);
                Assert.Equal(seed.Version, await Value("SELECT version_id FROM runs WHERE id=$1", child));
                Assert.Equal(0L, await Value("SELECT count(*) FROM task_attempts WHERE run_id=$1", child));
                using var response = await server.Send(HttpMethod.Get, $"/api/v1/runs/{child:D}", null!, RerunOperator(server, seed.Group));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var snapshot = await Json(response);
                Assert.Equal(seed.Snapshot.RunId, snapshot.GetProperty("parentRunId").GetGuid());
                Assert.Equal(seed.Snapshot.RunId, snapshot.GetProperty("reusedNodes").GetProperty("build").GetGuid());
                Assert.Equal(seed.Snapshot.RunId, snapshot.GetProperty("reusedNodes").GetProperty("deploy-eu").GetGuid());
            }
        }
        finally { await leader.StopAsync(TestContext.Current.CancellationToken); }
        Assert.Equal(latest, await Value("SELECT active_version_id FROM workflows WHERE id=$1", seed.Request.WorkflowId!));
        Assert.Empty(server.Failures);
    }
}
