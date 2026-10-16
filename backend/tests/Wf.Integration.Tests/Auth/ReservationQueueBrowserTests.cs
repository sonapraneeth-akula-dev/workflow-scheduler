using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact(Timeout = 180_000)]
    [Trait("Category", "FrontendBrowser")]
    public async Task Reservation_queue_browser_reads_real_API_and_tracks_FIFO_positions_and_grant_accessibly()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_RESERVATION_QUEUE_BROWSER") == "true",
            "Set WF_RESERVATION_QUEUE_BROWSER=true and WF_FRONTEND_ROOT after installing frontend browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "reservation-queue-browser.ts")));
        var current = await QueueScenario();
        var seed = current.Seed;
        var group = current.Group;
        try
        {
            await using var server = await Server.Start(database.DataSource, live: true, configure: app =>
            {
                app.MapReservationQueueEndpoints();
                app.MapPost("/api/test/reservation-queue/reset", async () =>
                {
                    await current.DisposeAsync();
                    current = await QueueScenario(seed: seed, group: group);
                    return Results.Ok(new
                    {
                        runId = current.Second.Snapshot.RunId,
                        blockingRunId = current.Owner.Snapshot.RunId,
                        blockingReservationId = current.Blocker.Id
                    });
                }).RequireAuthorization();
                app.MapPost("/api/test/reservation-queue/advance", async () =>
                {
                    await current.Store.AppendAsync(current.First, EventEntity.Run, null, (int)RunStatus.Failed,
                        DateTimeOffset.UtcNow, QueueToken);
                    await current.Manager.SweepQueueAsync(QueueToken);
                    return Results.NoContent();
                }).RequireAuthorization();
                app.MapPost("/api/test/reservation-queue/release", async () =>
                {
                    await current.ReleaseOwner();
                    await current.Manager.SweepQueueAsync(QueueToken);
                    return Results.NoContent();
                }).RequireAuthorization();
            });
            var start = new ProcessStartInfo("bun")
            {
                WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine("scripts", "reservation-queue-browser.ts"));
            start.Environment["WF_QUEUE_API"] = server.Api;
            start.Environment["WF_QUEUE_TOKEN"] = server.Token(group: group);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(QueueToken);
            var stderr = process.StandardError.ReadToEndAsync(QueueToken);
            try
            {
                await process.WaitForExitAsync(QueueToken);
                Assert.True(process.ExitCode == 0, $"Reservation queue browsers failed.\n{await stdout}\n{await stderr}");
                if (Environment.GetEnvironmentVariable("WF_RESERVATION_BROWSER_LOG") is { } log)
                    await File.WriteAllTextAsync(log, await stdout + await stderr, QueueToken);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
            }
        }
        finally { await current.DisposeAsync(); }
    }
}
