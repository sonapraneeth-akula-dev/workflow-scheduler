using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;

var connectionString = Environment.GetEnvironmentVariable("WF_RECOVERY_DATABASE")
    ?? throw new InvalidOperationException("WF_RECOVERY_DATABASE is required.");
var holder = Environment.GetEnvironmentVariable("WF_RECOVERY_HOLDER")
    ?? throw new InvalidOperationException("WF_RECOVERY_HOLDER is required.");
var holdRunning = Environment.GetEnvironmentVariable("WF_RECOVERY_HOLD") == "true";
var abortMode = Environment.GetEnvironmentVariable("WF_RECOVERY_ABORT") == "true";
await using var source = NpgsqlDataSource.Create(connectionString);
using var leadership = new LeaseManager(source, new(holder, new Uri("http://localhost:5300")),
    NullLogger<LeaseManager>.Instance);
var store = new RunStore(source, NullLogger<RunStore>.Instance, leadership);
using var recovery = new RunRecovery(source, store, leadership, TimeProvider.System, NullLogger<RunRecovery>.Instance);
await using var appender = new GroupCommitAppender(source, NullLogger<GroupCommitAppender>.Instance, leadership: leadership);
await leadership.StartAsync(CancellationToken.None);
await recovery.StartAsync(CancellationToken.None);
var active = new Dictionary<Guid, RunState>();
long epoch = 0;
while (true)
{
    if (leadership.Current is not { } lease || !recovery.IsReady)
    {
        active.Clear();
        epoch = 0;
        await Task.Delay(25);
        continue;
    }
    if (epoch != lease.Epoch || abortMode)
    {
        active = recovery.RecoveredRuns.ToDictionary(pair => pair.Key, pair => pair.Value.State);
        epoch = lease.Epoch;
    }
    try
    {
        foreach (var run in active.Values)
        {
            var snapshot = run.Snapshot;
            RunEvent? change = null;
            if (snapshot.Status == RunStatus.Queued)
                change = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow);
            else if (snapshot.Status == RunStatus.Running)
            {
                var node = snapshot.Nodes["node"];
                var task = snapshot.Tasks["node/task"];
                if (node == ExecutionStatus.Pending) change = Node(ExecutionStatus.Ready);
                else if (node == ExecutionStatus.Ready) change = Node(ExecutionStatus.Dispatched);
                else if (node == ExecutionStatus.Dispatched) change = Node(ExecutionStatus.Running);
                else if (task == ExecutionStatus.Pending) change = TaskChange(ExecutionStatus.Ready);
                else if (task == ExecutionStatus.Ready) change = TaskChange(ExecutionStatus.Dispatched);
                else if (task == ExecutionStatus.Dispatched) change = TaskChange(ExecutionStatus.Running);
                else if (task == ExecutionStatus.Running && !holdRunning && !abortMode) change = TaskChange(ExecutionStatus.Succeeded);
                else if (task == ExecutionStatus.Succeeded && node == ExecutionStatus.Running) change = Node(ExecutionStatus.Succeeded);
                else if (node == ExecutionStatus.Succeeded)
                    change = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Succeeded, DateTimeOffset.UtcNow);
                RunEvent Node(ExecutionStatus status) => run.CreateEvent(EventEntity.Node, "node", (int)status, DateTimeOffset.UtcNow);
                RunEvent TaskChange(ExecutionStatus status) => run.CreateEvent(EventEntity.Task, "node/task", (int)status, DateTimeOffset.UtcNow);
            }
            else if (abortMode && snapshot.Status == RunStatus.Aborting && !holdRunning)
            {
                if (snapshot.Tasks["node/task"] is ExecutionStatus.Running or ExecutionStatus.Dispatched)
                    change = run.CreateEvent(EventEntity.Task, "node/task", (int)ExecutionStatus.Cancelled, DateTimeOffset.UtcNow);
                else if (snapshot.Nodes["node"] == ExecutionStatus.Running)
                    change = run.CreateEvent(EventEntity.Node, "node", (int)ExecutionStatus.Cancelled, DateTimeOffset.UtcNow);
                else
                    change = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Aborted, DateTimeOffset.UtcNow);
            }
            if (change is null) continue;
            await appender.AppendAsync(change);
            run.Apply(change);
        }
    }
    catch (NotLeaderException) { active.Clear(); epoch = 0; }
    await Task.Delay(50);
}

namespace Wf.Recovery.Fixture { public sealed class FixtureMarker; }
