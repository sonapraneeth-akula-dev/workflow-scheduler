using System.Text.Json;
using Npgsql;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using Wf.Engine.Core.Metrics;

namespace Wf.ControlPlane.Api;

internal static class CommittedRunRead
{
    // Call inside a repeatable-read transaction so header, replay and attempt attribution share one view.
    internal static async Task<RunSnapshot> Load(NpgsqlConnection connection, RunStore store, Guid runId,
        CancellationToken token)
    {
        await using var header = new NpgsqlCommand("""
            SELECT status,last_seq,snapshot_seq FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1
            """, connection);
        header.Parameters.AddWithValue(runId);
        await using var reader = await header.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            await reader.DisposeAsync();
            return await Pending(connection, runId, token);
        }
        var status = reader.GetInt16(0);
        var last = reader.GetInt64(1);
        var saved = reader.GetInt64(2);
        await reader.DisposeAsync();
        var snapshot = (await store.LoadAsync(connection, runId, false, token)).Snapshot;
        if (saved < 0 || saved > last || snapshot.RunId != runId ||
            snapshot.LastSequence != last || (int)snapshot.Status != status)
            throw new InvalidDataException();
        return snapshot;
    }

    private static async Task<RunSnapshot> Pending(NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT payload::text,created_at FROM commands WHERE run_id=$1 AND kind=0 AND processed_at IS NULL
            """, connection);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException();
        var trigger = CommandInbox.ReadInitial(reader.GetString(0));
        var created = reader.GetFieldValue<DateTimeOffset>(1);
        if (trigger.InitialState.RunId != id || trigger.DeadlineAt <= created ||
            !WorkflowIdentity.TryGetTypeScope(trigger.WorkflowId, out _))
            throw new InvalidDataException();
        var snapshot = trigger.InitialState;
        if (snapshot.Metrics is not null) _ = RunMetricsResponse.From(snapshot);
        return RunState.Restore(snapshot with { Metrics = RunMetricsRecorder.Initialize(snapshot, created) }, [], _ => { }).Snapshot;
    }
}
