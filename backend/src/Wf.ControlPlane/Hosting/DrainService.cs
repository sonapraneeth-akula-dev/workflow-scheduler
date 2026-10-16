using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Hosting;

public sealed class DrainService(IHostApplicationLifetime lifetime, LiveSubscriptions subscriptions,
    TimeProvider time, TimeSpan propagationDelay, ILogger<DrainService> logger) : IHostedLifecycleService, IDisposable
{
    private CancellationTokenRegistration stopping;
    private int draining;
    private readonly Lazy<Task> liveDrain = new(() => subscriptions.DrainAsync(CancellationToken.None));

    public bool IsReady => Volatile.Read(ref draining) == 0 && !lifetime.ApplicationStopping.IsCancellationRequested;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // MapHub registers SignalR's synchronous shutdown first; this later callback runs before it.
        stopping = lifetime.ApplicationStopping.Register(() =>
        {
            BeginDrain();
            liveDrain.Value.GetAwaiter().GetResult();
        });
        return Task.CompletedTask;
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        BeginDrain();
        await liveDrain.Value.WaitAsync(cancellationToken);
        // Lifecycle callbacks run before Kestrel stops accepting connections.
        await Task.Delay(propagationDelay, time, cancellationToken);
    }

    private void BeginDrain()
    {
        if (Interlocked.Exchange(ref draining, 1) == 0)
            logger.LogInformation("API draining: readiness is unavailable; accepted HTTP requests will finish before shutdown.");
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => stopping.Dispose();
}

public static class DrainExtensions
{
    public static IServiceCollection AddWorkflowDrain(this IServiceCollection services,
        IConfiguration configuration, bool engineEnabled = false)
    {
        var delay = configuration.GetValue("Drain:PropagationDelay",
            engineEnabled ? TimeSpan.Zero : TimeSpan.FromSeconds(2));
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(10))
            throw new InvalidOperationException("Drain:PropagationDelay must be between zero and ten seconds.");
        services.AddSingleton(provider => new DrainService(provider.GetRequiredService<IHostApplicationLifetime>(),
            provider.GetRequiredService<LiveSubscriptions>(), provider.GetRequiredService<TimeProvider>(), delay,
            provider.GetRequiredService<ILogger<DrainService>>()));
        services.AddHostedService(provider => provider.GetRequiredService<DrainService>());
        return services;
    }

    public static IEndpointConventionBuilder MapWorkflowReadiness(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/healthz/ready", (DrainService drain) => drain.IsReady
            ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status503ServiceUnavailable).AllowAnonymous();
}
