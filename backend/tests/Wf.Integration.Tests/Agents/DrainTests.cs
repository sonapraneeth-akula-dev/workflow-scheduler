using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Checkpoints;
using Wf.Integration.Tests.Reports;
using FixtureAgent = Wf.Integration.Tests.Checkpoints.CheckpointProducerTests.Agent;
using ReportHost = Wf.Integration.Tests.Reports.ReportPublishTests.ReportHost;
using static Wf.Integration.Tests.Checkpoints.CheckpointProducerTests;

namespace Wf.Integration.Tests.Agents;

public sealed class DrainTests(ReportPublishDatabase database, ITestOutputHelper output) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(2, true, false)]
    public async Task Drain_real_SDK_finishes_or_physically_stops_work_before_release_and_safe_replacement(
        int mode, bool blockEngine, bool reconnect)
    {
        await using var host = await ReportHost.Start(database.DataSource,
            configureServices: builder => builder.Services.AddSingleton<AgentDrainService>()
                .AddSingleton<Wf.ControlPlane.Engine.ResourceSampleSink>());
        var archive = await ReportPublishTests.Package();
        host.App.MapGet("/drain-package.zip", () => Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        await using var first = new FixtureAgent(host, id);
        await Until(() => Task.FromResult(host.Registry.Find(id) is not null), first.Work);
        var session = new AgentSession(id, host.Registry.Find(id)!.SessionId);
        var attempt = await host.Seed(dispatch: false, task: "drain");
        var marker = Path.Combine(host.Root, "drain-marker");
        var command = new Dispatch
        {
            Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = "drain", Attempt = 1 },
            Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(host.Address, "/drain-package.zip").AbsoluteUri,
            VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { checkpointMarker = marker, amount = mode })),
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(3))
        };
        var reservations = new ReservationManager(database.DataSource, host.Runs, TimeProvider.System);
        var reservation = Assert.Single(await reservations.ReserveAsync(attempt.RunId, "$post", 1,
            new(pool: host.Pool), TimeSpan.FromMinutes(5), Token));
        using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry, new PlacementEngine(), TimeProvider.System);
        Assert.Equal(id, (await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
            new(attempt.RunId, "$post", "drain", 1, hash, new(new(pool: host.Pool))), command, Token)).Agent!.Id);
        await Until(() => Task.FromResult(File.Exists(marker)), first.Work);
        using var started = JsonDocument.Parse(await File.ReadAllTextAsync(marker, Token));
        var pid = started.RootElement.GetProperty("pid").GetInt32();
        var childPid = started.RootElement.GetProperty("childPid").ValueKind == JsonValueKind.Null ? (int?)null :
            started.RootElement.GetProperty("childPid").GetInt32();
        Assert.False(Exited(pid));
        var drains = host.App.Services.GetRequiredService<AgentDrainService>();
        var watch = Stopwatch.StartNew();
        var receipt = await drains.RequestAsync(id, mode == 0 ? 60 : 1, "drain integration", Guid.NewGuid().ToString("D"), "User", Token);
        Assert.Empty(await host.Runs.PendingAsync(session, Token));
        var other = await host.Seed(dispatch: false, task: "report");
        var forbidden = command.Clone();
        forbidden.Task = new() { RunId = other.RunId.ToString("D"), NodeKey = "$post", TaskKey = "report", Attempt = 1 };
        Assert.Null((await dispatcher.DispatchAsync(await host.Runs.LoadAsync(other.RunId, Token),
            new(other.RunId, "$post", "report", 1, hash, new(new(pool: host.Pool))), forbidden, Token)).Agent);
        Assert.Equal(1, first.Hosts.ProcessStartCount);
        if (mode != 0)
            await Until(() => Task.FromResult(File.Exists(marker + ".cancelled")), first.Work);
        Assert.Null((await drains.FindAsync(id, Token))!.ReleasedAt);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM reservations WHERE id=$1 AND released_at IS NULL", reservation.Id));
        await using var barrier = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await barrier.BeginTransactionAsync(Token);
        if (blockEngine)
        {
            await using var hold = new NpgsqlCommand("LOCK TABLE drain_requests IN ACCESS EXCLUSIVE MODE", barrier);
            await hold.ExecuteNonQueryAsync(Token);
        }
        try
        {
            if (mode != 2) await File.WriteAllTextAsync(marker + ".exit", "1", Token);
            if (blockEngine)
            {
                await Until(() => Task.FromResult(PendingDrain(first.Outbox) is not null), first.Work);
                Assert.True(Exited(pid));
                if (childPid is { } child) Assert.True(Exited(child));
                Assert.Empty(first.Commands.Inflight);
                Assert.Equal(1L, await Scalar("SELECT count(*) FROM reservations WHERE id=$1 AND released_at IS NULL", reservation.Id));
                Assert.Equal(receipt.Id.ToString("D"), PendingDrain(first.Outbox)!.Id);
                if (reconnect) await first.Stop();
            }
        }
        finally { await transaction.CommitAsync(Token); }
        await using var restored = reconnect ? new FixtureAgent(host, id) : null;
        await Until(async () => (await drains.FindAsync(id, Token))!.Status == "Released", restored?.Work ?? first.Work);
        if (restored is not null)
        {
            Assert.NotEqual(session.SessionId, host.Registry.Find(id)!.SessionId);
            Assert.Equal(0, restored.Hosts.ProcessStartCount);
        }
        Assert.True(Exited(pid));
        if (childPid is { } descendant) Assert.True(Exited(descendant));
        Assert.Empty(first.Commands.Inflight);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM reservations WHERE id=$1 AND released_at IS NULL", reservation.Id));
        Assert.DoesNotContain(host.Registry.DispatchableAgents(), agent => agent.Id == id);
        var spareId = Guid.NewGuid();
        await using var spare = new FixtureAgent(host, spareId);
        await Until(() => Task.FromResult(host.Registry.DispatchableAgents().Any(agent => agent.Id == spareId)), spare.Work);
        if (mode != 0)
        {
            var replacement = await dispatcher.ReplaceLostAsync(attempt.RunId, "$post/drain", 1, Token);
            Assert.True(replacement == AgentLossDisposition.Replaced,
                $"Replacement={replacement}; persisted=" + await Scalar("""
                    SELECT jsonb_build_object('status',status,'error',error,'category',failure_category)::text
                    FROM task_attempts WHERE run_id=$1 AND attempt=1
                    """, attempt.RunId));
            await Until(async () => (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks["$post/drain"] ==
                ExecutionStatus.Succeeded, spare.Work);
            await Until(() => Task.FromResult(spare.Commands.Inflight.Count == 0), spare.Work);
            Assert.Equal((short)0, await Scalar("SELECT author_retries FROM task_attempts WHERE run_id=$1 AND attempt=2", attempt.RunId));
            Assert.Equal((short)1, await Scalar("SELECT platform_retries FROM task_attempts WHERE run_id=$1 AND attempt=2", attempt.RunId));
            Assert.Equal(1, spare.Hosts.ProcessStartCount);
        }
        var snapshot = (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot;
        Assert.All(snapshot.Tasks.Values, status => Assert.Equal(ExecutionStatus.Succeeded, status));
        Assert.Equal(mode == 0 ? 1L : 2L, await Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1", attempt.RunId));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), $"Drain exceeded bounded physical cleanup: {watch.Elapsed}.");
        output.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}; mode={mode}; blocked={blockEngine}; reconnect={reconnect}; " +
            $"physical-release-and-success={watch.Elapsed.TotalSeconds:F3}s; original={pid}; child={childPid}; logicalFailures=0.");
    }

    private async Task<object?> Scalar(string sql, Guid id)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(Token);
    }

    private static DrainCompleted? PendingDrain(Wf.Agent.AgentOutbox outbox)
    {
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message)
        {
            sequence = message.Sequence;
            if (message.DrainCompleted is { } result) return result;
        }
        return null;
    }

    private static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
