using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Tests.Hosting;

public sealed class EngineShutdownTests
{
    [Fact]
    public async Task ShutdownClosesAppenderBeforeContainerDisposalAndRepeatedStopIsSafe()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Username=unused;Timeout=1");
        await using var appender = new GroupCommitAppender(source, NullLogger<GroupCommitAppender>.Instance);
        var service = new EngineShutdownService(appender);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => appender.AppendAsync(
            new RunEvent(Guid.CreateVersion7(), 1, DateTimeOffset.UtcNow, EventEntity.Run, null, (int)RunStatus.Running),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoppedLeaseCannotReacquireEvenThroughExplicitRenewal()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Username=unused;Timeout=1");
        using var manager = new LeaseManager(source, new("stopped", new Uri("http://localhost:5200")),
            NullLogger<LeaseManager>.Instance);
        await manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(await manager.TryAcquireOrRenewAsync(TestContext.Current.CancellationToken));
        Assert.Null(manager.Current);
        Assert.Throws<NotLeaderException>(manager.RequireLease);
    }
}
