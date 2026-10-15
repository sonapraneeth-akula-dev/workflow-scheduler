namespace Wf.ControlPlane.Engine.Agents;

public sealed class AgentRegistryService(AgentRegistry registry, TimeProvider timeProvider,
    ILogger<AgentRegistryService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await registry.LoadAsync(cancellationToken);
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
                var expired = await registry.SweepAsync(stoppingToken);
                if (expired != 0) logger.LogWarning("{Count} agents became Unhealthy after missed heartbeats.", expired);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
