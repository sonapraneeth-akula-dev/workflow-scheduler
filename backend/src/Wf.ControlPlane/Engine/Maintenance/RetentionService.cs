using Azure;
using Npgsql;
using Wf.ControlPlane.Engine.Leadership;

namespace Wf.ControlPlane.Engine.Maintenance;

public sealed class RetentionService(RetentionPurger purger, PartitionMaintenance partitions, LeaseManager leadership,
    TimeProvider time, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), time);
        DateOnly? prepared = null;
        DateOnly? inventoried = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (leadership.IsLeader)
                {
                    var now = time.GetUtcNow();
                    var today = DateOnly.FromDateTime(now.UtcDateTime);
                    if (prepared != today)
                    {
                        await partitions.EnsureAsync(stoppingToken);
                        prepared = today;
                    }
                    if (RetentionPurger.IsOffPeak(now))
                    {
                        var removed = await purger.PurgeBatchAsync(stoppingToken);
                        if (inventoried != today)
                        {
                            await purger.QueueOrphanArtifactsAsync(stoppingToken);
                            inventoried = today;
                        }
                        for (var index = 0; index < 32 && await purger.DeletePendingBlobsAsync(stoppingToken); index++) { }
                        if (removed == 0)
                            await partitions.DropExpiredEmptyAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (NotLeaderException) { }
            catch (Exception error) when (error is NpgsqlException or IOException or RequestFailedException or InvalidDataException or
                NotSupportedException or TimeoutException or ArgumentException)
            { logger.LogError(error, "Retention maintenance did not complete; durable cleanup will retry."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
