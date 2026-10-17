using Npgsql;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Engine.Recovery;

internal static class RerunHandler
{
    internal static async Task ApplyAsync(NpgsqlConnection connection, TriggerRunCommand command,
        DateTimeOffset createdAt, string createdBy, CancellationToken token)
    {
        CommandInbox.Validate(command, rerun: true);
        var state = RunState.Restore(command.InitialState, [], _ => { });
        await RunStore.CreateAsync(connection, state, command.WorkflowId, command.VersionId, command.DeadlineAt,
            createdAt, createdBy, token, command.SeriesId, RunTrigger.Rerun, command.ParentRunId);
        foreach (var (node, reused) in command.ReusedNodes!)
            await RunStore.ExecuteAsync(connection, """
                UPDATE node_runs SET reused_from_run_id=$3,outputs=$4::jsonb
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND node_key=$2
                """, token, state.Snapshot.RunId, node, reused.ReusedFromRunId, reused.Outputs.GetRawText());
    }
}
