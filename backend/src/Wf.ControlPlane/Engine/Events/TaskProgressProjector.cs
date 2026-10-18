using System.Text.Json;
using Npgsql;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Progress;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Engine.Events;

internal static class TaskProgressProjector
{
    internal static async Task AppendAsync(NpgsqlConnection connection, RunState run, TaskProgress progress,
        DateTimeOffset receivedAt, CancellationToken token)
    {
        var value = TaskProgressProtocol.Validate(progress);
        var key = $"{progress.Task.NodeKey}/{progress.Task.TaskKey}";
        uint previous = 0;
        await using (var history = new NpgsqlCommand("""
            SELECT payload FROM run_events WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                AND entity_key=$2 AND attempt=$3 AND kind=2 ORDER BY seq
            """, connection))
        {
            history.Parameters.AddWithValue(run.Snapshot.RunId);
            history.Parameters.AddWithValue(key);
            history.Parameters.AddWithValue(checked((short)progress.Task.Attempt));
            await using var rows = await history.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
            {
                var change = JsonSerializer.Deserialize(rows.GetFieldValue<byte[]>(0), RunStoreJson.Default.RunEvent)
                    ?? throw new InvalidDataException("Persisted task event is null.");
                if (change.Progress is not { } recorded) continue;
                previous = Math.Max(previous, recorded.Sequence);
                if (recorded.Sequence != value.Sequence) continue;
                if (recorded != value) throw new ArgumentException("Task progress sequence already has different content.");
                return;
            }
        }
        if (value.Sequence != previous + 1)
            throw new ArgumentException("Task progress sequence is not contiguous for this attempt.");
        if (run.Snapshot.Abort is { } abort && receivedAt < abort.At) receivedAt = abort.At;
        var update = new RunEvent(run.Snapshot.RunId, checked(run.Snapshot.LastSequence + 1), receivedAt,
            EventEntity.Task, key, (int)run.Snapshot.Tasks[key])
        {
            Attempt = checked((int)progress.Task.Attempt), Progress = value
        };
        await RunStore.AppendAsync(connection, run, update, token);
    }
}
