using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Engine.Core.StateMachine;

namespace Wf.ControlPlane.Engine.Maintenance;

public static class RollupWriter
{
    public static Guid[] TerminalRunIds(IEnumerable<RunEvent> changes) => changes
        .Where(change => change.Entity == EventEntity.Run && TransitionTable.IsTerminal((RunStatus)change.State))
        .Select(change => change.RunId).Distinct().Order().ToArray();

    public static async Task RefreshAsync(NpgsqlConnection connection, Guid[] runIds, CancellationToken token)
    {
        var buckets = new List<(DateOnly Day, string Workflow)>();
        await using (var query = new NpgsqlCommand("""
            SELECT DISTINCT run_day,workflow_id COLLATE "C" AS workflow_id FROM runs
            WHERE run_day=ANY(SELECT uuid_v7_run_day(id) FROM unnest($1::uuid[]) id)
                AND id=ANY($1) AND status BETWEEN 3 AND 6
            ORDER BY run_day,workflow_id
            """, connection))
        {
            query.Parameters.AddWithValue(runIds);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) buckets.Add((reader.GetFieldValue<DateOnly>(0), reader.GetString(1)));
        }
        // All callers acquire bucket locks in the same order, including multi-run group commits.
        foreach (var (day, workflow) in buckets)
        {
            await using var refresh = new NpgsqlCommand("SELECT refresh_daily_rollups($1,$2)", connection);
            refresh.Parameters.AddWithValue(day);
            refresh.Parameters.AddWithValue(workflow);
            await refresh.ExecuteNonQueryAsync(token);
        }
    }
}
