using Npgsql;
using Wf.Contracts.Definition;

namespace Wf.ControlPlane.Api;

public sealed record RunFailureDetail(string Type, string Category, string Message, string NodeKey,
    string? TaskKey, int? Attempt, string Impact, string? RuntimeCategory, string? RuleId, bool MessageRecorded);
public sealed record RunFailureGroup(string Type, string Category, string Impact, int Count);
public sealed record RunFailureSummary(int Count, int RecoveredCount, RunFailureDetail Primary,
    IReadOnlyList<RunFailureGroup> Groups);

public static class RunFailures
{
    public static RunFailureSummary? Summarize(IReadOnlyList<RunFailureDetail> failures)
    {
        if (failures.Count == 0) return null;
        if (failures.Any(failure => failure.Impact is not ("Failed" or "Retrying" or "Optional" or "Recovered")))
            throw new InvalidDataException("Unknown failure impact.");
        var ordered = failures.OrderBy(failure => failure.Impact switch
        {
            "Failed" => 0, "Retrying" => 1, "Optional" => 2, "Recovered" => 3,
            _ => throw new InvalidDataException("Unknown failure impact.")
        }).ToArray();
        return new(ordered.Length, ordered.Count(failure => failure.Impact == "Recovered"), ordered[0],
            ordered.GroupBy(failure => (failure.Type, failure.Category, failure.Impact))
                .Select(group => new RunFailureGroup(group.Key.Type, group.Key.Category, group.Key.Impact, group.Count()))
                .ToArray());
    }

    public static async Task<IReadOnlyDictionary<Guid, RunFailureSummary>> Summaries(HttpContext context,
        NpgsqlDataSource source, IEnumerable<Guid> runIds)
    {
        var ids = runIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, RunFailureSummary>();
        await using var connection = await source.OpenConnectionAsync(context.RequestAborted);
        var details = await Read(connection, context.RequestServices.GetRequiredService<FailureCatalog>(), ids, context.RequestAborted);
        return details.ToDictionary(pair => pair.Key, pair => Summarize(pair.Value)!);
    }

    public static async Task<IReadOnlyDictionary<Guid, List<RunFailureDetail>>> Read(NpgsqlConnection connection,
        FailureCatalog catalog, Guid[] runIds, CancellationToken token)
    {
        var result = new Dictionary<Guid, List<RunFailureDetail>>();
        if (runIds.Length == 0) return result;
        await using var command = new NpgsqlCommand("""
            WITH selected AS (
                SELECT r.id,r.workflow_id,r.run_day,r.status FROM unnest($1::uuid[]) w(id)
                JOIN runs r ON r.run_day=uuid_v7_run_day(w.id) AND r.id=w.id
            ), failures AS (
                SELECT r.id,r.workflow_id,a.task_key,a.attempt,a.failure_category,a.error,a.ended_at,
                    split_part(a.task_key,'/',1) AS node_key,
                    CASE WHEN latest.status=5 THEN 'Recovered'
                        WHEN r.status<3 AND (latest.retry_at IS NOT NULL OR latest.attempt>a.attempt AND latest.status<5) THEN 'Retrying'
                        WHEN n.status=6 OR r.status=3 OR a.status=6 THEN 'Optional'
                        ELSE 'Failed' END AS impact
                FROM selected r
                JOIN task_attempts a ON a.run_day=r.run_day AND a.run_id=r.id AND a.status IN (6,7,8)
                LEFT JOIN node_runs n ON n.run_day=a.run_day AND n.run_id=a.run_id
                    AND n.node_key=split_part(a.task_key,'/',1)
                JOIN LATERAL (
                    SELECT l.attempt,l.status,l.retry_at FROM task_attempts l
                    WHERE l.run_day=a.run_day AND l.run_id=a.run_id AND l.task_key=a.task_key
                    ORDER BY l.attempt DESC LIMIT 1
                ) latest ON true
                UNION ALL
                SELECT r.id,r.workflow_id,NULL,NULL,n.failure_category,NULL,n.ended_at,n.node_key,
                    CASE WHEN n.status=6 OR r.status=3 THEN 'Optional' ELSE 'Failed' END
                FROM selected r JOIN node_runs n ON n.run_day=r.run_day AND n.run_id=r.id AND n.status IN (6,7,8)
                WHERE NOT EXISTS (
                    SELECT 1 FROM task_attempts a WHERE a.run_day=n.run_day AND a.run_id=n.run_id
                        AND split_part(a.task_key,'/',1)=n.node_key AND a.status IN (6,7,8)
                        AND (n.failure_category IS NULL OR a.failure_category IS NOT DISTINCT FROM n.failure_category)
                )
            )
            SELECT id,workflow_id,task_key,attempt,failure_category,error,node_key,impact
            FROM failures ORDER BY id,ended_at DESC NULLS LAST,node_key,task_key,attempt DESC
            """, connection);
        command.Parameters.AddWithValue(runIds);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetGuid(0);
            var workflow = reader.GetString(1).Split('/');
            if (workflow.Length != 3) throw new InvalidDataException("Invalid failure workflow identity.");
            var runtime = reader.IsDBNull(4) ? (FailureCategory?)null : (FailureCategory)reader.GetInt16(4);
            var recorded = reader.IsDBNull(5) ? null : reader.GetString(5);
            var classification = catalog.Classify(workflow[0], recorded, runtime);
            var messageRecorded = !string.IsNullOrWhiteSpace(recorded);
            var message = messageRecorded ? recorded!.Trim() : MissingMessage(runtime);
            var detail = new RunFailureDetail(classification.Type, classification.Category, message,
                reader.GetString(6), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt16(3), reader.GetString(7), runtime?.ToString(),
                classification.RuleId, messageRecorded);
            if (!result.TryGetValue(id, out var failures)) result.Add(id, failures = []);
            failures.Add(detail);
        }
        return result;
    }

    private static string MissingMessage(FailureCategory? category) => category switch
    {
        FailureCategory.AgentLost => "The agent became unavailable. No original failure message was recorded.",
        FailureCategory.Timeout => "Execution exceeded its deadline. No original failure message was recorded.",
        FailureCategory.Placement => "No eligible machine was assigned before the placement deadline. No original failure message was recorded.",
        FailureCategory.ResourceLimit => "Execution exceeded a resource limit. No original failure message was recorded.",
        FailureCategory.PlatformError => "The execution platform reported a failure. No original failure message was recorded.",
        _ => "No failure message was recorded."
    };
}
