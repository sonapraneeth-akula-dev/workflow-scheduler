using Wf.Contracts.Schedules;
using Wf.Engine.Core.Scheduling;

namespace Wf.Engine.Core.Tests;

public sealed class OverlapPolicyTests
{
    [Theory]
    [InlineData(ScheduleOverlapPolicy.Skip, 0, ScheduleOverlapDecision.Start)]
    [InlineData(ScheduleOverlapPolicy.Skip, 1, ScheduleOverlapDecision.Skip)]
    [InlineData(ScheduleOverlapPolicy.Skip, 2, ScheduleOverlapDecision.Skip)]
    [InlineData(ScheduleOverlapPolicy.Skip, int.MaxValue, ScheduleOverlapDecision.Skip)]
    [InlineData(ScheduleOverlapPolicy.Queue, 0, ScheduleOverlapDecision.Start)]
    [InlineData(ScheduleOverlapPolicy.Queue, 1, ScheduleOverlapDecision.Queue)]
    [InlineData(ScheduleOverlapPolicy.Queue, 2, ScheduleOverlapDecision.Queue)]
    [InlineData(ScheduleOverlapPolicy.Queue, int.MaxValue, ScheduleOverlapDecision.Queue)]
    [InlineData(ScheduleOverlapPolicy.Concurrent, 0, ScheduleOverlapDecision.Start)]
    [InlineData(ScheduleOverlapPolicy.Concurrent, 1, ScheduleOverlapDecision.Start)]
    [InlineData(ScheduleOverlapPolicy.Concurrent, 2, ScheduleOverlapDecision.Start)]
    [InlineData(ScheduleOverlapPolicy.Concurrent, int.MaxValue, ScheduleOverlapDecision.Start)]
    public void Every_policy_distinguishes_idle_and_active_series(ScheduleOverlapPolicy policy, int activeRuns,
        ScheduleOverlapDecision expected) => Assert.Equal(expected, OverlapPolicy.Decide(activeRuns, policy));

    [Fact]
    public void Default_is_skip_only_when_a_previous_run_is_nonterminal()
    {
        Assert.Equal(ScheduleOverlapDecision.Start, OverlapPolicy.Decide(0));
        Assert.Equal(ScheduleOverlapDecision.Skip, OverlapPolicy.Decide(1));
    }

    [Fact]
    public void Queued_candidate_starts_only_after_earlier_runs_drain()
    {
        Assert.Equal(ScheduleOverlapDecision.Start, OverlapPolicy.Decide(0, ScheduleOverlapPolicy.Queue));
        Assert.Equal(ScheduleOverlapDecision.Queue, OverlapPolicy.Decide(1, ScheduleOverlapPolicy.Queue));
        Assert.Equal(ScheduleOverlapDecision.Queue, OverlapPolicy.Decide(2, ScheduleOverlapPolicy.Queue));
        Assert.Equal(ScheduleOverlapDecision.Queue, OverlapPolicy.Decide(1, ScheduleOverlapPolicy.Queue));
        Assert.Equal(ScheduleOverlapDecision.Start, OverlapPolicy.Decide(0, ScheduleOverlapPolicy.Queue));
    }

    [Theory]
    [InlineData(ScheduleOverlapPolicy.Skip)]
    [InlineData(ScheduleOverlapPolicy.Queue)]
    [InlineData(ScheduleOverlapPolicy.Concurrent)]
    public void Independent_series_and_policy_changes_share_no_mutable_state(ScheduleOverlapPolicy policy)
    {
        var busy = OverlapPolicy.Decide(1, policy);
        Assert.Equal(ScheduleOverlapDecision.Start, OverlapPolicy.Decide(0, policy));
        Assert.Equal(busy, OverlapPolicy.Decide(1, policy));
        Assert.Equal(ScheduleOverlapDecision.Start, OverlapPolicy.Decide(1, ScheduleOverlapPolicy.Concurrent));
        Assert.Equal(ScheduleOverlapDecision.Skip, OverlapPolicy.Decide(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Invalid_counts_are_not_silently_treated_as_idle(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlapPolicy.Decide(count));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 0)]
    [InlineData(int.MaxValue, 1)]
    public void Unknown_policy_is_rejected_even_for_an_idle_series(int policy, int activeRuns) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlapPolicy.Decide(activeRuns, (ScheduleOverlapPolicy)policy));
}
