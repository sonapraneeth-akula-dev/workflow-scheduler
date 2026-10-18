using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts.Definition;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Catalog_browser_keeps_team_and_type_viewers_isolated_with_actual_jwt_and_accessible_views()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_PERMISSION_BROWSER") == "true",
            "Set WF_PERMISSION_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "catalog-isolation-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        var teamGroup = Guid.NewGuid();
        var typeGroup = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, teamGroup, true);
        await Save(new(seed.Team,
            [new(seed.Team, "release", "Permitted release workflows", new([]), [new(RegistryRole.Viewer, typeGroup)]),
             new(seed.Team, "sibling", "Team-only sibling workflows", new([]))],
            RoleBindings: [new(RegistryRole.Viewer, teamGroup)],
            Presentation: new(TypeOrder: ["sibling", "release"])));
        var ownWorkflow = seed.Type + "/catalog-visible";
        var otherWorkflow = seed.OtherType + "/catalog-private";
        await CatalogWorkflow(ownWorkflow);
        await CatalogWorkflow(seed.Team + "/sibling/sibling-visible");
        await CatalogWorkflow(otherWorkflow);
        var ownRun = await CatalogBrowserRun(ownWorkflow);
        var otherRun = await CatalogBrowserRun(otherWorkflow);
        await using var server = await Server.Start(database.DataSource, live: true);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "catalog-isolation-browser.ts"));
        start.Environment["WF_CATALOG_API"] = server.Api;
        start.Environment["WF_CATALOG_TEAM_TOKEN"] = server.Token(group: teamGroup);
        start.Environment["WF_CATALOG_TYPE_TOKEN"] = server.Token(group: typeGroup);
        start.Environment["WF_CATALOG_ADMIN_TOKEN"] = server.Token(role: "Admin");
        start.Environment["WF_CATALOG_TEAM"] = seed.Team;
        start.Environment["WF_CATALOG_OTHER_TEAM"] = seed.Other;
        start.Environment["WF_CATALOG_RUN"] = ownRun.ToString("D");
        start.Environment["WF_CATALOG_OTHER_RUN"] = otherRun.ToString("D");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Catalogue isolation browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private async Task<Guid> CatalogBrowserRun(string workflow)
    {
        await using var version = database.DataSource.CreateCommand("SELECT latest_version_id FROM workflows WHERE id=$1");
        version.Parameters.AddWithValue(workflow);
        var versionId = Assert.IsType<Guid>(await version.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        var state = new RunState(Guid.CreateVersion7(),
            [new NodeDefinition("work", [], [new TaskDefinition("task")])],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), _ => { });
        await new RunStore(database.DataSource, NullLogger<RunStore>.Instance).CreateAsync(state, workflow,
            versionId, DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
        return state.Snapshot.RunId;
    }
}
