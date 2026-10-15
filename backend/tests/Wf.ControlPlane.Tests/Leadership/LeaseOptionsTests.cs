using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.ControlPlane.Engine.Leadership;

namespace Wf.ControlPlane.Tests.Leadership;

public sealed class LeaseOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void LeaseHolderRequiresAnInstanceIdentity(string? holder)
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Username=unused");
        Assert.Throws<ArgumentException>(() => new LeaseManager(source, new(holder!, new Uri("http://localhost:5000")),
            NullLogger<LeaseManager>.Instance));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("ftp://localhost")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?query=1")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com/#fragment")]
    public void LeaseHintMustBeASafeEngineOrigin(string endpoint)
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Username=unused");
        Assert.Throws<ArgumentException>(() => new LeaseManager(source, new("instance", new Uri(endpoint)),
            NullLogger<LeaseManager>.Instance));
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5100")]
    [InlineData("https://engine.example.com")]
    public void LeaseStartsWithoutAuthorityAndUsesRequiredCadence(string endpoint)
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Username=unused");
        using var manager = new LeaseManager(source, new("instance", new Uri(endpoint)), NullLogger<LeaseManager>.Instance);
        Assert.False(manager.IsLeader);
        Assert.Null(manager.Current);
        Assert.Equal("", Assert.Throws<NotLeaderException>(manager.RequireLease).LeaderHint);
        Assert.Equal(TimeSpan.FromSeconds(3), LeaseManager.RenewalPeriod);
        Assert.Equal(TimeSpan.FromSeconds(10), LeaseManager.TimeToLive);
    }
}
