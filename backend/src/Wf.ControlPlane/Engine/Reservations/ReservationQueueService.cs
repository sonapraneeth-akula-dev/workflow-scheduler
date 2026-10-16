using Npgsql;
using Wf.ControlPlane.Engine.Leadership;

namespace Wf.ControlPlane.Engine.Reservations;

public sealed class ReservationQueueService(ReservationManager manager, TimeProvider time,
    ILogger<ReservationQueueService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await manager.SweepQueueAsync(stoppingToken); }
                catch (NotLeaderException) { logger.LogDebug("Reservation queue paused because this engine is not leader."); }
                catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
                {
                    logger.LogError(exception, "Reservation queue will retry when durable storage is available.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
