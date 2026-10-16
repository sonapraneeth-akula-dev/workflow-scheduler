using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wf.ControlPlane.Hosting;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Tests.Hosting;

public sealed class DrainServiceTests
{
    [Fact]
    public async Task Stop_signal_changes_readiness_immediately_before_lifecycle_shutdown()
    {
        using var lifetime = new Lifetime();
        await using var services = Services(lifetime, "00:00:00");
        var drain = services.GetRequiredService<DrainService>();
        await drain.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(drain.IsReady);
        lifetime.StopApplication();
        Assert.False(drain.IsReady);
        await drain.StoppingAsync(TestContext.Current.CancellationToken);
        await drain.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(drain.IsReady);
    }

    [Fact]
    public async Task Direct_host_stop_withholds_readiness_and_waits_for_route_propagation_before_server_stop()
    {
        using var lifetime = new Lifetime();
        await using var services = Services(lifetime, "00:00:00.05");
        var drain = services.GetRequiredService<DrainService>();
        await drain.StartAsync(TestContext.Current.CancellationToken);
        var stopping = drain.StoppingAsync(TestContext.Current.CancellationToken);
        Assert.False(drain.IsReady);
        Assert.False(stopping.IsCompleted);
        await stopping;
        Assert.False(drain.IsReady);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Engine_default_does_not_delay_lease_handover_but_API_only_default_waits_for_routing(bool engine)
    {
        using var lifetime = new Lifetime();
        await using var services = Services(lifetime, null, engine);
        var drain = services.GetRequiredService<DrainService>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stop = drain.StoppingAsync(deadline.Token);
        Assert.Equal(engine, stop.IsCompletedSuccessfully);
        if (engine) await stop;
        else
        {
            await deadline.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        }
    }

    [Theory]
    [InlineData("-00:00:01")]
    [InlineData("00:00:10.001")]
    [InlineData("invalid")]
    public void Invalid_propagation_delays_fail_configuration(string delay)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Drain:PropagationDelay"] = delay }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkflowDrain(configuration));
    }

    private static ServiceProvider Services(Lifetime lifetime, string? delay, bool engine = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<LiveSubscriptions>();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton(TimeProvider.System);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Drain:PropagationDelay"] = delay }).Build();
        services.AddWorkflowDrain(configuration, engine);
        return services.BuildServiceProvider();
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
}
