using System.Diagnostics;
using System.Text.Json;
using NpgsqlTypes;
using Wf.Sdk;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed partial class TerminalNotificationTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Subscriptions_browser_real_Postgres_three_scopes_channels_owner_isolation_denial_cleanup_and_accessibility()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_SUBSCRIPTION_BROWSER") == "true",
            "Set WF_SUBSCRIPTION_BROWSER=true and WF_FRONTEND_ROOT with installed browser dependencies.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "subscription-browser.ts")));
        var scope = await Registry();
        var version = Guid.CreateVersion7();
        var workflow = Workflow.Define(scope.Workflow, "1.0.0", builder =>
            builder.Type("type").Node("work", node => node.Task("task", _ => { })));
        using var manifest = JsonDocument.Parse(ManifestWriter.WriteUtf8(workflow));
        await Execute("""
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,$2,'1.0.0',$3,'packages/'||repeat('a',64)||'.zip',decode(repeat('a',64),'hex'),1,
                'sources/'||repeat('b',64),decode(repeat('b',64),'hex'),1,repeat('c',40),'subscription-browser','subscription-browser','1.0.0');
            """, version, scope.Workflow, new Npgsql.NpgsqlParameter
            { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = manifest.RootElement.GetRawText() });
        await Execute("UPDATE workflows SET active_version_id=$2,latest_version_id=$2 WHERE id=$1", scope.Workflow, version);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, browser: true);
        var viewer = api.Jwt(scope.Group);
        var other = api.Jwt(scope.Group);
        var foreign = api.Jwt(Guid.NewGuid());
        var paging = api.Jwt(scope.Group);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "subscription-browser.ts"));
        start.Environment["WF_SUBSCRIPTION_API"] = api.Api;
        start.Environment["WF_SUBSCRIPTION_FIXTURE"] = JsonSerializer.Serialize(new
        {
            workflow = scope.Workflow, type = scope.Type, scheduleId = scope.Schedule,
            viewerToken = viewer, otherToken = other, foreignToken = foreign, pagingToken = paging
        });
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(8));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Subscription browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        var logs = await stdout + await stderr;
        foreach (var jwt in new[] { viewer, other, foreign, paging }) Assert.DoesNotContain(jwt, logs);
    }
}
