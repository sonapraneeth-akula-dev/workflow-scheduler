using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts.Manifest;
using Wf.Contracts.Definition;
using Wf.Contracts.Registry;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Engine.Dispatch;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task VersionActivation_browser_confirms_exact_targets_preserves_pins_and_denies_Viewer_with_real_jwt()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_ACTIVATION_BROWSER") == "true",
            "Set WF_ACTIVATION_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "version-activation-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var viewer = Guid.NewGuid();
        var registry = request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)!;
        request = request with
        {
            Registry = JsonSerializer.SerializeToElement(registry with
            {
                Types = [registry.Types[0] with { RoleBindings = [new(RegistryRole.Viewer, viewer)] }]
            }, TeamRegistryJsonContext.Default.TeamRegistry)
        };
        using var first = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        using var second = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var latest = (await Json(second)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => HasVersion(server.Versions, request.WorkflowId!, latest));
        var state = Initial(server.Versions.GetActive(request.WorkflowId!));
        await new RunStore(database.DataSource, NullLogger<RunStore>.Instance).CreateAsync(state, request.WorkflowId!,
            latest, DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);

        var conflicting = await Request(server);
        var manifest = conflicting.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        manifest = manifest with { Variables = [new("count", VariableType.Int, true, JsonSerializer.SerializeToElement(1))] };
        conflicting = conflicting with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest)
        };
        using var oldConflict = await server.Post(conflicting);
        Assert.Equal(HttpStatusCode.Created, oldConflict.StatusCode);
        var conflictTarget = (await Json(oldConflict)).GetProperty("workflowVersion").GetGuid();
        var schedules = new ScheduleFile([new("timer", "Etc/UTC", Interval: TimeSpan.FromMinutes(1),
            Variables: ImmutableDictionary<string, JsonElement>.Empty.Add("count", JsonSerializer.SerializeToElement("private-value")))]);
        using var newConflict = await server.Post(conflicting with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest with
            {
                Version = "2.0.0",
                Variables = [new("count", VariableType.String, true, JsonSerializer.SerializeToElement("default"))]
            }, WorkflowManifestJsonContext.Default.WorkflowManifest),
            Schedules = JsonSerializer.Serialize(schedules, ScheduleFileJsonContext.Default.ScheduleFile),
            ScheduleSourcePath = "schedules.json"
        });
        Assert.Equal(HttpStatusCode.Created, newConflict.StatusCode);
        var conflictLatest = (await Json(newConflict)).GetProperty("workflowVersion").GetGuid();
        var scheduleXmin = await Value("SELECT xmin::text FROM schedules WHERE workflow_id=$1", conflicting.WorkflowId!);
        var config = await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", conflicting.WorkflowId!);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "version-activation-browser.ts"));
        start.Environment["WF_ACTIVATION_API"] = server.Api;
        start.Environment["WF_ACTIVATION_FIXTURE"] = JsonSerializer.Serialize(new
        {
            workflowId = request.WorkflowId, original, latest, runId = state.Snapshot.RunId,
            conflictWorkflowId = conflicting.WorkflowId, conflictTarget, conflictLatest,
            adminToken = server.Token("Admin", "user", "api.read"),
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
            Assert.True(process.ExitCode == 0, $"Version activation browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        Assert.Equal(latest, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(latest, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(latest, await Value("SELECT version_id FROM runs WHERE id=$1", state.Snapshot.RunId));
        Assert.Equal(6L, await ActivationAudits(request.WorkflowId!));
        Assert.Equal(0L, await ActivationAudits(conflicting.WorkflowId!));
        Assert.Equal(conflictLatest, await Value("SELECT active_version_id FROM workflows WHERE id=$1", conflicting.WorkflowId!));
        Assert.Equal(config, await Value("SELECT active_schedule_config_id FROM workflows WHERE id=$1", conflicting.WorkflowId!));
        Assert.Equal(scheduleXmin, await Value("SELECT xmin::text FROM schedules WHERE workflow_id=$1", conflicting.WorkflowId!));
        Assert.Empty(server.Failures);
    }
}
