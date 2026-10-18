using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Engine.Events;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task LiveConnection_browser_shares_one_actual_MessagePack_socket_and_ordered_scoped_stores()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_LIVE_BROWSER") == "true",
            "Set WF_LIVE_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and Playwright browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "live-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        await using var server = await Server.Start(database.DataSource, live: true);
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var writer = WriteAsync();
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "live-browser.ts"));
        start.Environment["WF_LIVE_API"] = server.Api;
        start.Environment["WF_LIVE_TOKEN"] = server.Token(group: group);
        start.Environment["WF_LIVE_RUN"] = seed.Run.ToString("D");
        start.Environment["WF_LIVE_TYPE"] = seed.Type;
        start.Environment["WF_LIVE_DENIED"] = seed.OtherType;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(exited, writer) == writer) await writer;
            await exited;
            Assert.True(process.ExitCode == 0, $"Live browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            await lifetime.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await writer;
            await Task.WhenAll(stdout, stderr);
        }

        async Task WriteAsync()
        {
            var sequence = 1;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await appender.AppendAsync(Enumerable.Range(sequence, 32).Select(value => Change(seed.Run, value)).ToArray(),
                        lifetime.Token);
                    sequence += 32;
                    await Task.Delay(15, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}
