using Wf.ControlPlane.Engine.Events;

namespace Wf.ControlPlane.Hosting;

/// <summary>Register after the lease service and before its event producers; hosted services stop in reverse order.</summary>
public sealed class EngineShutdownService(GroupCommitAppender appender) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Keep the lease renewing until every accepted event has finished its bounded drain.
    public Task StopAsync(CancellationToken cancellationToken) => appender.DisposeAsync().AsTask();
}
