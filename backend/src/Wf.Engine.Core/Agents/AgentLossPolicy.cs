namespace Wf.Engine.Core.Agents;

public sealed record AgentLossDecision(bool Unhealthy, bool AttemptsLost);

public static class AgentLossPolicy
{
    public static readonly TimeSpan UnhealthyAfter = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(30);

    public static AgentLossDecision Evaluate(DateTimeOffset lastHeartbeatAt, DateTimeOffset now)
    {
        if (lastHeartbeatAt.Offset != TimeSpan.Zero || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Agent liveness timestamps must use UTC.");
        var age = now - lastHeartbeatAt;
        return new(age >= UnhealthyAfter, age >= LostAfter);
    }
}
