using Npgsql;
using Wf.Contracts.Resources;
using Wf.Engine.Core;
using Wf.Engine.Core.Metrics;
using Wf.Engine.Core.StateMachine;

namespace Wf.ControlPlane.Engine;

internal sealed record CommittedRunTelemetry(WorkflowMetricIdentity Identity, RunSnapshot Snapshot,
    ResourceAttemptSummary[] Resources)
{
    public void Emit() => WorkflowTelemetry.RecordCompletion(Identity, Snapshot, Resources);

    public static async Task<CommittedRunTelemetry?> CaptureAsync(NpgsqlConnection connection, RunSnapshot snapshot,
        CancellationToken token)
    {
        if (!WorkflowTelemetry.Enabled || !TransitionTable.IsTerminal(snapshot.Status)) return null;
        await using var owner = new NpgsqlCommand(
            "SELECT workflow_id FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1", connection);
        owner.Parameters.AddWithValue(snapshot.RunId);
        var workflow = (string?)await owner.ExecuteScalarAsync(token)
            ?? throw new InvalidDataException("A terminal run has no workflow identity.");
        await using var query = new NpgsqlCommand("""
            SELECT task_key,attempt,resource_sample_count,resource_avg_cpu_percent,resource_max_cpu_percent,
                resource_peak_ram_bytes,resource_network_sent_bytes,resource_network_received_bytes,
                resource_cpu_observed_seconds,resource_network_observed_seconds
            FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND resource_sample_count>0
            ORDER BY task_key,attempt
            """, connection);
        query.Parameters.AddWithValue(snapshot.RunId);
        await using var reader = await query.ExecuteReaderAsync(token);
        var resources = new List<ResourceAttemptSummary>();
        while (await reader.ReadAsync(token))
            resources.Add(new(reader.GetString(0), reader.GetInt16(1), null, reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.GetDouble(8), reader.GetDouble(9),
                null, null, null));
        return new(WorkflowMetricIdentity.FromWorkflowId(workflow, snapshot.Metadata.Type), snapshot, resources.ToArray());
    }
}
