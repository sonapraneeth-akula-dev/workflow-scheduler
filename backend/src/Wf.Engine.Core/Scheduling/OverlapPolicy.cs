using Wf.Contracts.Schedules;

namespace Wf.Engine.Core.Scheduling;

public enum ScheduleOverlapDecision { Start, Skip, Queue }

public static class OverlapPolicy
{
    // Count other nonterminal runs in this series, including queued runs; exclude the candidate.
    public static ScheduleOverlapDecision Decide(int activeRuns, ScheduleOverlapPolicy policy = ScheduleOverlapPolicy.Skip)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeRuns);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (activeRuns == 0 || policy == ScheduleOverlapPolicy.Concurrent) return ScheduleOverlapDecision.Start;
        return policy == ScheduleOverlapPolicy.Skip ? ScheduleOverlapDecision.Skip : ScheduleOverlapDecision.Queue;
    }
}
