using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task PublicApiContract_CLI_does_not_advertise_or_send_UI_only_schedule_mutations_even_with_Admin_token()
    {
        var requests = 0;
        await using var server = await Server.Start(database.DataSource, configure: app => app.MapFallback(() =>
        {
            Interlocked.Increment(ref requests);
            return Results.StatusCode(418);
        }));
        var executable = Path.Combine(AppContext.BaseDirectory, "wf.dll");
        Assert.True(File.Exists(executable), "The referenced public CLI must be built before contract acceptance.");
        var help = await Run(["--help"]);
        Assert.Contains("Usage: wf", help.Output);
        Assert.DoesNotMatch(@"(?im)^Usage: wf schedules? .*\b(create|edit|delete|pause|resume|preview)\b", help.Output);
        foreach (var root in new[] { "schedule", "schedules" })
        foreach (var action in new[] { "create", "edit", "delete", "pause", "resume", "preview" })
        {
            var result = await Run([root, action]);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Usage: wf", result.Output);
        }
        Assert.Equal(0, Volatile.Read(ref requests));

        async Task<(int ExitCode, string Output)> Run(string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(executable);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["WF_ENGINE_URL"] = server.Api;
            var jwt = server.Token(role: "Admin");
            start.Environment["WF_ACCESS_TOKEN"] = jwt;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(MachineToken);
            var stderr = process.StandardError.ReadToEndAsync(MachineToken);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(deadline.Token);
                var output = await stdout + await stderr;
                Assert.DoesNotContain(jwt, output);
                return (process.ExitCode, output);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
            }
        }
    }
}
