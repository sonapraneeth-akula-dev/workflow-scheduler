using Wf.Contracts.Runs;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Engine.Scheduler;
using Wf.Engine.Core.Scheduling;
using Wf.Validation.Schedules;

namespace Wf.ControlPlane.Tests;

public sealed class ScheduleCatchUpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleExpression Minute = ScheduleExpression.Create(null, TimeSpan.FromMinutes(1), "Etc/UTC");

    [Theory]
    [InlineData(ScheduleCatchUpPolicy.Latest, 1)]
    [InlineData(ScheduleCatchUpPolicy.None, 0)]
    public void Ten_missed_slots_produce_ten_durable_decisions_and_at_most_one_latest_catch_up(ScheduleCatchUpPolicy policy, int runs)
    {
        var clock = new Clock(Start.AddMinutes(9).AddSeconds(30));
        var plan = new ScheduleCatchUp(clock).Plan(Start, Minute, policy, TestContext.Current.CancellationToken);
        Assert.Equal(10, plan.CatchUp.Fires.Length);
        Assert.All(plan.CatchUp.Fires, fire => Assert.Equal(ScheduleFireOutcome.Missed, fire.Outcome));
        Assert.Equal(runs, plan.CatchUp.CatchUp is null ? 0 : 1);
        if (runs == 1) Assert.Equal(Start.AddMinutes(9), plan.CatchUp.CatchUp!.SourceFireAt);
        Assert.Equal(Start.AddMinutes(10), plan.NextFireAt);
        Assert.Equal(1, clock.Reads);
    }

    [Theory]
    [InlineData(0, ScheduleFireOutcome.Started)]
    [InlineData(1, ScheduleFireOutcome.Missed)]
    public void Default_two_second_grace_is_inclusive_at_the_exact_boundary(int overdueMicroseconds, ScheduleFireOutcome outcome)
    {
        var planner = new ScheduleCatchUp(new Clock(Start.AddSeconds(2).AddMicroseconds(overdueMicroseconds)));
        Assert.Equal(TimeSpan.FromSeconds(2), planner.Grace);
        var plan = planner.Plan(Start, Minute, ScheduleCatchUpPolicy.Latest, Token);
        Assert.Equal(outcome, Assert.Single(plan.CatchUp.Fires).Outcome);
        Assert.Equal(outcome == ScheduleFireOutcome.Missed, plan.CatchUp.CatchUp is not null);
    }

    [Fact]
    public void Configured_grace_changes_lateness_not_the_intended_cursor()
    {
        var planner = new ScheduleCatchUp(new Clock(Start.AddSeconds(15)), TimeSpan.FromSeconds(20));
        var plan = planner.Plan(Start, Minute, ScheduleCatchUpPolicy.Latest, Token);
        Assert.Equal(TimeSpan.FromSeconds(20), planner.Grace);
        Assert.Equal(ScheduleFireOutcome.Started, Assert.Single(plan.CatchUp.Fires).Outcome);
        Assert.Null(plan.CatchUp.CatchUp);
        Assert.Equal(Start.AddMinutes(1), plan.NextFireAt);
    }

    [Fact]
    public void Mixed_missed_and_recent_slots_preserve_both_history_and_the_newest_missed_source()
    {
        var planner = new ScheduleCatchUp(new Clock(Start.AddMinutes(9).AddSeconds(1)));
        var plan = planner.Plan(Start, Minute, ScheduleCatchUpPolicy.Latest, Token);
        Assert.Equal(9, plan.CatchUp.Fires.Count(fire => fire.Outcome == ScheduleFireOutcome.Missed));
        Assert.Equal(Start.AddMinutes(9), Assert.Single(plan.CatchUp.Fires, fire => fire.Outcome == ScheduleFireOutcome.Started).FireAt);
        Assert.Equal(Start.AddMinutes(8), plan.CatchUp.CatchUp!.SourceFireAt);
    }

    [Fact]
    public void Replaying_the_committed_next_cursor_does_not_plan_another_catch_up()
    {
        var planner = new ScheduleCatchUp(new Clock(Start.AddMinutes(9).AddSeconds(30)));
        var next = planner.Plan(Start, Minute, ScheduleCatchUpPolicy.Latest, Token).NextFireAt!.Value;
        var replay = planner.Plan(next, Minute, ScheduleCatchUpPolicy.Latest, Token);
        Assert.Empty(replay.CatchUp.Fires);
        Assert.Null(replay.CatchUp.CatchUp);
        Assert.Equal(next, replay.NextFireAt);
    }

    [Fact]
    public void Fall_fold_keeps_distinct_UTC_slots_in_missed_history()
    {
        var start = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
        var planner = new ScheduleCatchUp(new Clock(start.AddHours(2).AddSeconds(30)));
        var plan = planner.Plan(start, ScheduleExpression.Create("*/30 * * * *", null, "America/New_York"),
            ScheduleCatchUpPolicy.Latest, Token);
        Assert.Equal(5, plan.CatchUp.Fires.Length);
        Assert.Equal(5, plan.CatchUp.Fires.Select(fire => fire.FireAt).Distinct().Count());
        Assert.Equal(start.AddHours(2), plan.CatchUp.CatchUp!.SourceFireAt);
        Assert.Equal(start.AddHours(2.5), plan.NextFireAt);
    }

    [Fact]
    public void Invalid_configuration_and_cancellation_fail_explicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleCatchUp(new Clock(Start), TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentNullException>(() => new ScheduleCatchUp(null!));
        var planner = new ScheduleCatchUp(new Clock(Start));
        Assert.Throws<ArgumentNullException>(() => planner.Plan(Start, null!, ScheduleCatchUpPolicy.Latest, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => planner.Plan(Start, Minute, (ScheduleCatchUpPolicy)99, Token));
        Assert.Throws<OperationCanceledException>(() => planner.Plan(Start, Minute, ScheduleCatchUpPolicy.Latest, new(true)));
    }

    [Fact]
    public void Persisted_trigger_values_remain_stable_for_manual_scheduled_and_catch_up_runs()
    {
        Assert.Equal(0, (short)RunTrigger.Manual);
        Assert.Equal(1, (short)RunTrigger.Schedule);
        Assert.Equal(2, (short)RunTrigger.CatchUp);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return now; }
    }
}
