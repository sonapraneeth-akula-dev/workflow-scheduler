using Wf.Contracts.Schedules;
using Wf.Engine.Core.Scheduling;

namespace Wf.Engine.Core.Tests;

public sealed class CatchUpPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ScheduleCatchUpPolicy.Latest, 1)]
    [InlineData(ScheduleCatchUpPolicy.None, 0)]
    public void Ten_missed_slots_are_preserved_and_at_most_one_run_is_caught_up(
        ScheduleCatchUpPolicy policy, int catchUpCount)
    {
        var clock = new TestClock(Start.AddMinutes(10).AddSeconds(1));
        var plan = new SchedulerLoop(clock).Plan(Start.AddMinutes(1), NextMinute, TimeSpan.Zero, policy,
            TestContext.Current.CancellationToken);

        Assert.Equal(10, plan.CatchUp.Fires.Length);
        Assert.All(plan.CatchUp.Fires, fire => Assert.Equal(ScheduleFireOutcome.Missed, fire.Outcome));
        Assert.Equal(Enumerable.Range(1, 10).Select(index => Start.AddMinutes(index)),
            plan.CatchUp.Fires.Select(fire => fire.FireAt));
        Assert.Equal(catchUpCount, plan.CatchUp.CatchUp is null ? 0 : 1);
        if (plan.CatchUp.CatchUp is { } run)
        {
            Assert.Equal(Start.AddMinutes(10), run.SourceFireAt);
            Assert.Equal(ScheduleFireOutcome.CaughtUp, run.Outcome);
            Assert.Equal("catch-up", run.Trigger);
        }
        Assert.Equal(Start.AddMinutes(11), plan.NextFireAt);
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public void Default_is_latest_only_and_catch_up_does_not_replace_the_missed_slot()
    {
        var plan = CatchUpPolicy.Evaluate([Start], Start.AddSeconds(1), TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new ScheduleFireDecision(Start, ScheduleFireOutcome.Missed), Assert.Single(plan.Fires));
        Assert.Equal(Start, Assert.IsType<CatchUpRun>(plan.CatchUp).SourceFireAt);
    }

    [Fact]
    public void Exact_grace_boundary_is_due_and_only_older_slots_are_missed()
    {
        var now = Start.AddMinutes(1);
        var plan = CatchUpPolicy.Evaluate([Start.AddTicks(-1), Start, now], now, TimeSpan.FromMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([ScheduleFireOutcome.Missed, ScheduleFireOutcome.Started, ScheduleFireOutcome.Started],
            plan.Fires.Select(fire => fire.Outcome));
        Assert.Equal(Start.AddTicks(-1), plan.CatchUp!.SourceFireAt);
    }

    [Fact]
    public void No_miss_means_no_catch_up_even_with_latest()
    {
        var plan = CatchUpPolicy.Evaluate([Start], Start, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ScheduleFireOutcome.Started, Assert.Single(plan.Fires).Outcome);
        Assert.Null(plan.CatchUp);
    }

    [Fact]
    public void Future_or_exhausted_cursor_never_calls_expression()
    {
        var scheduler = new SchedulerLoop(new TestClock(Start));
        var future = scheduler.Plan(Start.AddTicks(1), Never, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);
        var exhausted = scheduler.Plan(null, Never, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(future.CatchUp.Fires);
        Assert.Null(future.CatchUp.CatchUp);
        Assert.Equal(Start.AddTicks(1), future.NextFireAt);
        Assert.Empty(exhausted.CatchUp.Fires);
        Assert.Null(exhausted.CatchUp.CatchUp);
        Assert.Null(exhausted.NextFireAt);

        static DateTimeOffset? Never(DateTimeOffset _) => throw new InvalidOperationException("Not due.");
    }

    [Fact]
    public void Exhaustion_after_a_due_fire_keeps_its_history()
    {
        var plan = new SchedulerLoop(new TestClock(Start.AddSeconds(1)))
            .Plan(Start, _ => null, TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(plan.CatchUp.Fires);
        Assert.NotNull(plan.CatchUp.CatchUp);
        Assert.Null(plan.NextFireAt);
    }

    [Fact]
    public void Restart_with_committed_next_cursor_does_not_replay_or_shift_slots()
    {
        var clock = new TestClock(Start.AddMinutes(10).AddSeconds(1));
        var first = new SchedulerLoop(clock).Plan(Start.AddMinutes(1), NextMinute, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);
        var restarted = new SchedulerLoop(clock).Plan(first.NextFireAt, NextMinute, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(restarted.CatchUp.Fires);
        Assert.Null(restarted.CatchUp.CatchUp);
        clock.Now = Start.AddMinutes(11);
        var later = new SchedulerLoop(clock).Plan(restarted.NextFireAt, NextMinute, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new ScheduleFireDecision(Start.AddMinutes(11), ScheduleFireOutcome.Started),
            Assert.Single(later.CatchUp.Fires));
        Assert.Null(later.CatchUp.CatchUp);
        Assert.Equal(Start.AddMinutes(12), later.NextFireAt);
    }

    [Fact]
    public void Planning_uses_one_clock_snapshot_even_if_expression_advances_time()
    {
        var clock = new TestClock(Start);
        var plan = new SchedulerLoop(clock).Plan(Start, fire =>
        {
            clock.Now = clock.Now.AddDays(1);
            return fire.AddMinutes(1);
        }, TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(plan.CatchUp.Fires);
        Assert.Equal(Start.AddMinutes(1), plan.NextFireAt);
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public void Instants_are_normalized_to_utc_without_changing_identity()
    {
        var fire = Start.ToOffset(TimeSpan.FromHours(-7));
        var plan = new SchedulerLoop(new TestClock(Start.AddSeconds(1)))
            .Plan(fire, next => next.AddMinutes(1).ToOffset(TimeSpan.FromHours(3)), TimeSpan.Zero,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.Zero, Assert.Single(plan.CatchUp.Fires).FireAt.Offset);
        Assert.Equal(TimeSpan.Zero, plan.CatchUp.CatchUp!.SourceFireAt.Offset);
        Assert.Equal(TimeSpan.Zero, plan.NextFireAt!.Value.Offset);
        Assert.Equal(Start, plan.CatchUp.CatchUp.SourceFireAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonadvancing_expression_fails_instead_of_hanging_or_dropping_slots(int ticks) =>
        Assert.Throws<InvalidOperationException>(() => new SchedulerLoop(new TestClock(Start))
            .Plan(Start, fire => fire.AddTicks(ticks), TimeSpan.Zero,
                cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void Expression_failure_propagates_without_a_partial_success_plan() =>
        Assert.Throws<FormatException>(() => new SchedulerLoop(new TestClock(Start))
            .Plan(Start, _ => throw new FormatException("Invalid schedule."), TimeSpan.Zero,
                cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void Unordered_duplicate_or_future_policy_input_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CatchUpPolicy.Evaluate([Start, Start], Start, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => CatchUpPolicy.Evaluate([Start, Start.AddTicks(-1)], Start, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => CatchUpPolicy.Evaluate([Start.AddTicks(1)], Start, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Invalid_configuration_is_rejected_even_without_due_fires()
    {
        var scheduler = new SchedulerLoop(new TestClock(Start));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Plan(null, NextMinute, TimeSpan.FromTicks(-1),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Plan(null, NextMinute, TimeSpan.Zero,
            (ScheduleCatchUpPolicy)99, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => scheduler.Plan(null, null!, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => CatchUpPolicy.Evaluate(null!, Start, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => new SchedulerLoop(null!));
    }

    [Fact]
    public void Cancellation_is_observed_before_work_and_between_slots()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SchedulerLoop(new TestClock(Start))
            .Plan(null, NextMinute, TimeSpan.Zero, cancellationToken: cancellation.Token));
        using var during = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => new SchedulerLoop(new TestClock(Start.AddMinutes(10)))
            .Plan(Start, fire =>
            {
                during.Cancel();
                return NextMinute(fire);
            }, TimeSpan.Zero, cancellationToken: during.Token));
    }

    private static DateTimeOffset? NextMinute(DateTimeOffset fire) => fire.AddMinutes(1);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
    }
}
