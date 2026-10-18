using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Reservations;
using Runtime = Wf.Integration.Tests.Execution.HostedRunExecutionTests.Runtime;

namespace Wf.Integration.Tests.Execution;

public sealed class TaskProgressTests(HostedExecutionDatabase database) : IClassFixture<HostedExecutionDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_replays_are_exactly_once_and_restart_preserves_progress_without_changing_attempt_scalars()
    {
        await using var runtime = await Create();
        var command = Assert.Single(await runtime.Pending());
        var before = (await runtime.Load()).Snapshot;
        var first = Progress(runtime, command);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Accept(runtime, first)));
        var after = (await runtime.Load()).Snapshot;
        Assert.Equal(before.LastSequence + 1, after.LastSequence);
        Assert.Equal(JsonSerializer.Serialize(before.Metrics), JsonSerializer.Serialize(after.Metrics));
        Assert.Equal((short)ExecutionStatus.Dispatched,
            await runtime.Scalar("SELECT status FROM task_attempts WHERE run_id=$1"));
        Assert.Equal(DBNull.Value, await runtime.Scalar("SELECT ended_at FROM task_attempts WHERE run_id=$1"));
        var rebuilt = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, timeProvider: runtime.Clock);
        var second = Progress(runtime, command, 2);
        second.Phase = RemoteOperationPhase.Submitted;
        await rebuilt.AcceptAsync(runtime.Session, new() { TaskProgress = second }, Token);
        await rebuilt.AcceptAsync(runtime.Session, new() { TaskProgress = first }, Token);
        Assert.Equal(before.LastSequence + 2, (await runtime.Load()).Snapshot.LastSequence);
        var conflict = first.Clone();
        conflict.Phase = RemoteOperationPhase.Failed;
        await Assert.ThrowsAsync<ArgumentException>(() => Accept(runtime, conflict));
        var gap = Progress(runtime, command, 4);
        await Assert.ThrowsAsync<ArgumentException>(() => Accept(runtime, gap));
        Assert.Equal(before.LastSequence + 2, (await runtime.Load()).Snapshot.LastSequence);
        await runtime.Complete(command);
        var completed = (await runtime.Load()).Snapshot.LastSequence;
        await Accept(runtime, Progress(runtime, command, 3));
        Assert.Equal(completed, (await runtime.Load()).Snapshot.LastSequence);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("expired")]
    [InlineData("closing")]
    [InlineData("attempt")]
    [InlineData("session")]
    public async Task Stale_progress_never_adds_events_or_releases_a_physical_reservation(string fence)
    {
        await using var runtime = await Create(reserve: fence is "expired" or "closing");
        var dispatch = Assert.Single(await runtime.Pending());
        var progress = Progress(runtime, dispatch);
        if (fence == "machine")
            await runtime.Execute("UPDATE agents SET reservation_epoch=reservation_epoch+1 WHERE id=$1", runtime.Session.AgentId);
        if (fence == "expired") runtime.Clock.Now = runtime.Clock.Now.AddMinutes(2);
        if (fence == "closing")
        {
            using var leases = new ReservationLeaseService(database.DataSource, runtime.Store, runtime.Clock,
                NullLogger<ReservationLeaseService>.Instance);
            await leases.ReleaseAsync(runtime.RunId, "work", "progress fence test", Token);
        }
        if (fence == "attempt")
        {
            _ = await runtime.AddAgent();
            await runtime.LoseAndReplace(runtime.Session);
        }
        if (fence == "session")
        {
            using var registry = new AgentRegistry(database.DataSource, runtime.Clock);
            var team = runtime.Workflow.Split('/')[0];
            var hello = new Hello
            {
                Id = runtime.Session.AgentId.ToString("D"), Name = team, Pool = team,
                Os = "windows", Arch = "x64", Cores = 4, RamMb = 16384, MaxTasks = 4,
                Version = "1.0.0", ProtocolVersion = 1
            };
            hello.Inflight.Add(dispatch.Task.Clone());
            _ = await registry.RegisterAsync(hello, Token);
        }
        var before = (await runtime.Load()).Snapshot.LastSequence;
        if (fence == "session") await Assert.ThrowsAsync<InvalidOperationException>(() => Accept(runtime, progress));
        else await Accept(runtime, progress);
        Assert.Equal(before, (await runtime.Load()).Snapshot.LastSequence);
        if (fence is "expired" or "closing")
            Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL"));
    }

    [Fact]
    public async Task Queued_preabort_progress_keeps_its_occurrence_time_without_rewinding_abort_or_task_state()
    {
        await using var runtime = await Create();
        var dispatch = Assert.Single(await runtime.Pending());
        var progress = Progress(runtime, dispatch);
        runtime.Clock.Now = runtime.Clock.Now.AddSeconds(1);
        var run = await runtime.Load();
        var abort = new AbortRecord("operator", "Operator", runtime.Clock.Now, "stop", AbortSource.API);
        await runtime.Store.AppendAsync(run, run.CreateEvent(EventEntity.Run, null,
            (int)RunStatus.Aborting, runtime.Clock.Now, abort), Token);
        var before = (await runtime.Load()).Snapshot;
        await Accept(runtime, progress);
        var after = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Aborting, after.Status);
        Assert.Equal(abort, after.Abort);
        Assert.Equal(JsonSerializer.Serialize(before.Metrics), JsonSerializer.Serialize(after.Metrics));
        Assert.Equal(before.LastSequence + 1, after.LastSequence);
        await using var query = database.DataSource.CreateCommand("""
            SELECT payload FROM run_events WHERE run_id=$1 ORDER BY seq DESC LIMIT 1
            """);
        query.Parameters.AddWithValue(runtime.RunId);
        using var json = JsonDocument.Parse(Assert.IsType<byte[]>(await query.ExecuteScalarAsync(Token)));
        Assert.Equal(progress.At.ToDateTimeOffset(), json.RootElement.GetProperty("Progress").GetProperty("OccurredAt").GetDateTimeOffset());
        Assert.True(json.RootElement.GetProperty("At").GetDateTimeOffset() >= abort.At);
    }

    private async Task<Runtime> Create(bool reserve = false)
    {
        var runtime = await Runtime.Create(database, w => w.Node("work", n => n.Task("task", _ => { })));
        try
        {
            if (reserve)
                await new ReservationManager(database.DataSource, runtime.Store, runtime.Clock).ReserveAsync(runtime.RunId,
                    "work", 1, new(machineIds: [runtime.Session.AgentId]), TimeSpan.FromMinutes(1), Token);
            await runtime.Tick();
            return runtime;
        }
        catch { await runtime.DisposeAsync(); throw; }
    }

    private static TaskProgress Progress(Runtime runtime, Dispatch dispatch, uint sequence = 1) => new()
    {
        Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch, ProgressSequence = sequence,
        Phase = RemoteOperationPhase.SubmissionPending,
        At = Timestamp.FromDateTimeOffset(new(runtime.Clock.Now.UtcTicks / 10 * 10, TimeSpan.Zero))
    };

    private static Task Accept(Runtime runtime, TaskProgress progress) =>
        runtime.Store.AcceptAsync(runtime.Session, new() { TaskProgress = progress }, Token);
}
