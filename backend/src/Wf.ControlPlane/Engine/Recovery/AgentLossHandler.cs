using Npgsql;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;

namespace Wf.ControlPlane.Engine.Recovery;

public sealed class AgentLossHandler(NpgsqlDataSource dataSource, Dispatcher dispatcher, LeaseManager leadership,
    TimeProvider time, ILogger<AgentLossHandler> logger) : BackgroundService
{
    public async Task<int> ProcessAsync(CancellationToken cancellationToken = default)
    {
        leadership.RequireLease();
        var pending = new List<(Guid RunId, string Key, short Attempt)>();
        await using (var command = dataSource.CreateCommand("""
            SELECT a.run_id,a.task_key,a.attempt FROM task_attempts a
            JOIN runs r ON r.run_day=a.run_day AND r.id=a.run_id
            WHERE a.status=7 AND a.failure_category=2 AND a.replacement_processed_at IS NULL AND r.status=1
                AND NOT EXISTS(SELECT 1 FROM task_attempts newer WHERE newer.run_day=a.run_day
                    AND newer.run_id=a.run_id AND newer.task_key=a.task_key AND newer.attempt>a.attempt)
            ORDER BY a.run_id,a.task_key LIMIT 100
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                pending.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt16(2)));
        var handled = 0;
        foreach (var (id, key, attempt) in pending)
        {
            var disposition = await dispatcher.ReplaceLostAsync(id, key, attempt, cancellationToken);
            if (disposition is AgentLossDisposition.Replaced or AgentLossDisposition.Failed)
            {
                handled++;
                logger.LogWarning("Agent loss for {RunId}/{TaskKey}/{Attempt}: {Disposition}.", id, key, attempt, disposition);
            }
        }
        return handled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!leadership.IsLeader) continue;
                try { await ProcessAsync(stoppingToken); }
                catch (NotLeaderException) { logger.LogInformation("Agent-loss re-placement paused after losing leadership."); }
                catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
                {
                    logger.LogError(exception, "Agent-loss re-placement will retry when durable storage is available.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
