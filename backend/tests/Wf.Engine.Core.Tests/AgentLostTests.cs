using Wf.Engine.Core.Agents;

namespace Wf.Engine.Core.Tests;

public sealed class AgentLostTests
{
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(14, false, false)]
    [InlineData(15, true, false)]
    [InlineData(29, true, false)]
    [InlineData(30, true, true)]
    [InlineData(60, true, true)]
    public void Heartbeat_age_has_exact_health_and_attempt_loss_boundaries(int seconds, bool unhealthy, bool lost)
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.Equal(new AgentLossDecision(unhealthy, lost), AgentLossPolicy.Evaluate(at, at.AddSeconds(seconds)));
    }

    [Fact]
    public void No_blanket_reconnect_wait_is_added_on_engine_recovery()
    {
        var heartbeat = DateTimeOffset.UnixEpoch;
        Assert.False(AgentLossPolicy.Evaluate(heartbeat, heartbeat.AddSeconds(30).AddTicks(-1)).AttemptsLost);
        Assert.True(AgentLossPolicy.Evaluate(heartbeat, heartbeat.AddSeconds(30)).AttemptsLost);
        Assert.False(AgentLossPolicy.Evaluate(heartbeat.AddSeconds(29), heartbeat.AddSeconds(30)).AttemptsLost);
    }

    [Fact]
    public void Nonutc_timestamps_are_rejected()
    {
        var local = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1));
        Assert.Throws<ArgumentException>(() => AgentLossPolicy.Evaluate(local, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => AgentLossPolicy.Evaluate(DateTimeOffset.UnixEpoch, local));
    }
}
