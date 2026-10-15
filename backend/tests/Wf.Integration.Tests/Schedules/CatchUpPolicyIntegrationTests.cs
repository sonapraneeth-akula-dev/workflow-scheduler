using System.Globalization;
using System.Text.Json;
using Wf.Contracts.Schedules;
using Wf.Engine.Core.Scheduling;
using Wf.Validation.Schedules;

namespace Wf.Integration.Tests.Schedules;

public sealed class CatchUpPolicyIntegrationTests
{
    [Theory]
    [InlineData(ScheduleCatchUpPolicy.Latest)]
    [InlineData(ScheduleCatchUpPolicy.None)]
    public void Real_interval_and_persisted_cursor_preserve_ten_misses_without_replaying(ScheduleCatchUpPolicy policy)
    {
        var first = Instant("2026-10-15T12:01:00Z");
        var now = Instant("2026-10-15T12:10:01Z");
        var expression = ScheduleExpression.Create(null, TimeSpan.FromMinutes(1), "Etc/UTC");
        var plan = new SchedulerLoop(new Clock(now)).Plan(first, expression.GetNextOccurrence, TimeSpan.Zero, policy,
            TestContext.Current.CancellationToken);

        Assert.Equal(10, plan.CatchUp.Fires.Length);
        Assert.All(plan.CatchUp.Fires, fire => Assert.Equal(ScheduleFireOutcome.Missed, fire.Outcome));
        Assert.Equal(Instant("2026-10-15T12:10:00Z"),
            plan.CatchUp.Fires[^1].FireAt);
        Assert.Equal(policy == ScheduleCatchUpPolicy.Latest, plan.CatchUp.CatchUp is not null);
        if (plan.CatchUp.CatchUp is { } run)
        {
            Assert.Equal("catch-up", run.Trigger);
            Assert.Equal(plan.CatchUp.Fires[^1].FireAt, run.SourceFireAt);
        }
        var restoredCursor = JsonSerializer.Deserialize<DateTimeOffset?>(JsonSerializer.Serialize(plan.NextFireAt));
        var restartedExpression = ScheduleExpression.Create(null, TimeSpan.FromMinutes(1), "Etc/UTC");
        var restarted = new SchedulerLoop(new Clock(now))
            .Plan(restoredCursor, restartedExpression.GetNextOccurrence, TimeSpan.Zero, policy,
                TestContext.Current.CancellationToken);
        Assert.Empty(restarted.CatchUp.Fires);
        Assert.Null(restarted.CatchUp.CatchUp);
        Assert.Equal(Instant("2026-10-15T12:11:00Z"), restarted.NextFireAt);
    }

    [Fact]
    public void Real_cron_fall_fold_retains_both_offsets_as_distinct_missed_slots()
    {
        var expression = ScheduleExpression.Create("*/30 1 * * *", null, "America/New_York");
        var first = expression.GetNextOccurrence(Instant("2026-11-01T04:59:59Z"));
        var now = Instant("2026-11-01T06:30:01Z");
        var plan = new SchedulerLoop(new Clock(now)).Plan(first, expression.GetNextOccurrence, TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "2026-11-01T05:00:00Z", "2026-11-01T05:30:00Z",
            "2026-11-01T06:00:00Z", "2026-11-01T06:30:00Z" }.Select(Instant),
            plan.CatchUp.Fires.Select(fire => fire.FireAt));
        Assert.All(plan.CatchUp.Fires, fire => Assert.Equal(ScheduleFireOutcome.Missed, fire.Outcome));
        Assert.Equal(Instant("2026-11-01T06:30:00Z"), plan.CatchUp.CatchUp!.SourceFireAt);
        Assert.Equal(Instant("2026-11-02T06:00:00Z"), plan.NextFireAt);
    }

    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
