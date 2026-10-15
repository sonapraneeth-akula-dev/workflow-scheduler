using Wf.ControlPlane.Engine.Leadership;

namespace Wf.ControlPlane.Engine.Agents;

public sealed class AgentRegistryService(AgentRegistry registry, TimeProvider timeProvider,
    ILogger<AgentRegistryService> logger, LeaseManager? leadership = null) : BackgroundService
{
    private long loadedEpoch;
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await registry.LoadAsync(cancellationToken);
        if (leadership is null)
            await registry.SweepAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (leadership is not null)
                    {
                        if (leadership.Current is not { } lease) continue;
                        if (loadedEpoch != lease.Epoch)
                        {
                            await registry.LoadAsync(stoppingToken);
                            loadedEpoch = lease.Epoch;
                        }
                    }
                    var expired = await registry.SweepAsync(stoppingToken);
                    if (expired != 0) logger.LogWarning("{Count} agents became Unhealthy after missed heartbeats.", expired);
                }
                catch (NotLeaderException)
                {
                    logger.LogInformation("Agent sweeper paused after losing leadership.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
