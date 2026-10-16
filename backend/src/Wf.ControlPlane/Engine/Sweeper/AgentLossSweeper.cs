using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core.Agents;

namespace Wf.ControlPlane.Engine.Sweeper;

public sealed record LostAttempt(Guid RunId, string TaskKey, short Attempt, Guid AgentId, DateTimeOffset LostAt);

public sealed class AgentLossSweeper(NpgsqlDataSource dataSource, AgentRegistry registry, TimeProvider time,
    ILogger<AgentLossSweeper> logger, LeaseManager? leadership = null) : BackgroundService
{
    public async Task<IReadOnlyList<LostAttempt>> SweepAsync(CancellationToken cancellationToken = default)
    {
        var lease = leadership?.RequireLease();
        await registry.SweepAsync(cancellationToken);
        var expired = new List<Guid>();
        await using (var command = dataSource.CreateCommand("SELECT id FROM agents WHERE last_heartbeat_at<=$1 ORDER BY id"))
        {
            command.Parameters.AddWithValue(time.GetUtcNow() - AgentLossPolicy.LostAfter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) expired.Add(reader.GetGuid(0));
        }
        var lost = new List<LostAttempt>();
        foreach (var id in expired)
            lost.AddRange(await ClassifyAsync(id, null, null, lease, cancellationToken));
        return lost;
    }

    public Task<IReadOnlyList<LostAttempt>> ReconcileAsync(AgentSession session, IEnumerable<AttemptRef> inflight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inflight);
        var reported = new HashSet<(Guid RunId, string TaskKey, short Attempt)>();
        foreach (var reference in inflight)
        {
            if (reference is null || !Guid.TryParse(reference.RunId, out var runId) || runId == Guid.Empty ||
                reference.Attempt is 0 or > 32767 || string.IsNullOrWhiteSpace(reference.NodeKey) ||
                string.IsNullOrWhiteSpace(reference.TaskKey) || reference.NodeKey.Contains('/') || reference.TaskKey.Contains('/') ||
                !reported.Add((runId, $"{reference.NodeKey}/{reference.TaskKey}", (short)reference.Attempt)))
                throw new ArgumentException("Reconciliation requires valid, distinct in-flight attempts.", nameof(inflight));
        }
        if (session.AgentId == Guid.Empty || session.SessionId == Guid.Empty)
            throw new ArgumentException("Reconciliation requires a registered agent session.", nameof(session));
        return ClassifyAsync(session.AgentId, session.SessionId, reported, leadership?.RequireLease(), cancellationToken);
    }

    private async Task<IReadOnlyList<LostAttempt>> ClassifyAsync(Guid agentId, Guid? sessionId,
        HashSet<(Guid RunId, string TaskKey, short Attempt)>? reported, LeaseToken? lease, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, cancellationToken);
        await using (var agent = new NpgsqlCommand("SELECT last_heartbeat_at,session_id FROM agents WHERE id=$1 FOR UPDATE", connection))
        {
            agent.Parameters.AddWithValue(agentId);
            await using var reader = await agent.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new KeyNotFoundException($"Agent '{agentId}' does not exist.");
            if (sessionId is { } expected && reader.GetGuid(1) != expected)
                throw new InvalidOperationException("Agent reconciliation was superseded by a new Hello.");
            if (reported is null && !AgentLossPolicy.Evaluate(reader.GetFieldValue<DateTimeOffset>(0), time.GetUtcNow()).AttemptsLost)
                return [];
        }
        var candidates = new List<(Guid RunId, string TaskKey, short Attempt)>();
        await using (var attempts = new NpgsqlCommand("""
            SELECT run_id,task_key,attempt FROM task_attempts
            WHERE agent_id=$1 AND status IN (2,3) ORDER BY run_id,task_key,attempt FOR UPDATE
            """, connection))
        {
            attempts.Parameters.AddWithValue(agentId);
            await using var reader = await attempts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetGuid(0), reader.GetString(1), reader.GetInt16(2));
                if (reported is null || !reported.Contains(key)) candidates.Add(key);
            }
        }
        var at = time.GetUtcNow();
        foreach (var (runId, taskKey, attempt) in candidates)
            await RunStore.ExecuteAsync(connection, """
                WITH lost AS (
                    UPDATE task_attempts SET status=$5,failure_category=$6,error=$7,ended_at=$4,
                        queue_wait_ms=GREATEST(0,EXTRACT(EPOCH FROM (COALESCE(started_at,$4)-queued_at))*1000)
                    WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
                    RETURNING run_day,run_id,task_key,attempt
                )
                DELETE FROM dispatch_outbox o USING lost l
                WHERE o.run_day=l.run_day AND o.run_id=l.run_id AND o.task_key=l.task_key AND o.attempt=l.attempt
                """, cancellationToken, runId, taskKey, attempt, at, (short)ExecutionStatus.Failed,
                (short)FailureCategory.AgentLost, reported is null
                    ? "Agent did not reconnect within 30 seconds of its last heartbeat."
                    : "Reconnected agent did not report this attempt in Hello.inflight.");
        await transaction.CommitAsync(cancellationToken);
        if (candidates.Count != 0)
            logger.LogWarning("Classified {Count} attempts on agent {AgentId} as AgentLost.", candidates.Count, agentId);
        return candidates.Select(key => new LostAttempt(key.RunId, key.TaskKey, key.Attempt, agentId, at)).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (leadership is not null && !leadership.IsLeader) continue;
                try { await SweepAsync(stoppingToken); }
                catch (NotLeaderException) { logger.LogInformation("Agent-loss sweeper paused after losing leadership."); }
                catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
                {
                    logger.LogError(exception, "Agent-loss classification will retry when durable storage is available.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
