using System.Globalization;
using Wf.Validation.Schedules;

namespace Wf.Validation.Tests;

public sealed class ScheduleExpressionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("* * * * *", 1)]
    [InlineData("", 1)]
    public void ExactlyOneScheduleIsRequired(string? cron, int? intervalSeconds)
    {
        Assert.Throws<ArgumentException>(() =>
            ScheduleExpression.Create(cron, intervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                "Etc/UTC"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not cron")]
    [InlineData("60 * * * *")]
    [InlineData("0 0 0 * * *")]
    [InlineData("H * * * *")]
    public void InvalidOrUnsupportedCronIsRejected(string cron)
    {
        Assert.Throws<ArgumentException>(() => ScheduleExpression.Create(cron, null, "Etc/UTC"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void IntervalMustBePositive(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScheduleExpression.Create(null, TimeSpan.FromTicks(ticks), "Etc/UTC"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Not/A_Zone")]
    [InlineData("Eastern Standard Time")]
    [InlineData("GMT Standard Time")]
    public void BothScheduleKindsRequireAValidIanaTimezone(string? timeZoneId)
    {
        Assert.ThrowsAny<ArgumentException>(() => ScheduleExpression.Create("* * * * *", null, timeZoneId!));
        Assert.ThrowsAny<ArgumentException>(() =>
            ScheduleExpression.Create(null, TimeSpan.FromSeconds(1), timeZoneId!));
    }

    [Fact]
    public void CronCursorIsExclusiveAndResultIsUtcEvenForOffsetInput()
    {
        var schedule = ScheduleExpression.Create("30 9 * * *", null, "America/New_York");
        var cursor = Instant("2026-01-15T09:30:00-05:00");

        Assert.Equal("30 9 * * *", schedule.Cron);
        Assert.Null(schedule.Interval);
        Assert.Equal("America/New_York", schedule.TimeZoneId);
        Assert.Equal(Instant("2026-01-16T14:30:00Z"), schedule.GetNextOccurrence(cursor));
        Assert.Equal(Instant("2026-01-15T14:30:00Z"), schedule.GetNextOccurrence(cursor.AddTicks(-1)));
        Assert.Equal(TimeSpan.Zero, schedule.GetNextOccurrence(cursor)!.Value.Offset);
    }

    [Fact]
    public void StandardCronMacrosAreSupported()
    {
        var schedule = ScheduleExpression.Create("@daily", null, "Etc/UTC");
        Assert.Equal(Instant("2026-01-02T00:00:00Z"),
            schedule.GetNextOccurrence(Instant("2026-01-01T00:00:00Z")));
    }

    [Theory]
    [InlineData("America/New_York", "30 2 * * *", "2026-03-08T06:59:59.9999999Z",
        "2026-03-08T07:00:00Z", "2026-03-09T06:30:00Z")]
    [InlineData("Europe/London", "30 1 * * *", "2026-03-29T00:59:59.9999999Z",
        "2026-03-29T01:00:00Z", "2026-03-30T00:30:00Z")]
    [InlineData("Australia/Sydney", "30 2 * * *", "2026-10-03T15:59:59.9999999Z",
        "2026-10-03T16:00:00Z", "2026-10-04T15:30:00Z")]
    public void SpringGapFixedTimeFiresAtFirstValidInstantWithoutSkippingDay(
        string zone, string cron, string after, string shifted, string followingDay)
    {
        var schedule = ScheduleExpression.Create(cron, null, zone);
        Assert.Equal([Instant(shifted), Instant(followingDay)], schedule.GetNextOccurrences(Instant(after), 2));
    }

    [Theory]
    [InlineData("America/New_York", "*/30 1-3 * * *", "2026-03-08T05:59:59.9999999Z",
        "2026-03-08T06:00:00Z", "2026-03-08T06:30:00Z", "2026-03-08T07:00:00Z", "2026-03-08T07:30:00Z")]
    [InlineData("Europe/London", "*/30 0-2 * * *", "2026-03-28T23:59:59.9999999Z",
        "2026-03-29T00:00:00Z", "2026-03-29T00:30:00Z", "2026-03-29T01:00:00Z", "2026-03-29T01:30:00Z")]
    [InlineData("Australia/Sydney", "*/30 1-3 * * *", "2026-10-03T14:59:59.9999999Z",
        "2026-10-03T15:00:00Z", "2026-10-03T15:30:00Z", "2026-10-03T16:00:00Z", "2026-10-03T16:30:00Z")]
    public void SpringGapIntervalLikeCronPreservesEveryRealHalfHour(
        string zone, string cron, string after, string first, string second, string third, string fourth)
    {
        var schedule = ScheduleExpression.Create(cron, null, zone);
        Assert.Equal([Instant(first), Instant(second), Instant(third), Instant(fourth)],
            schedule.GetNextOccurrences(Instant(after), 4));
    }

    [Theory]
    [InlineData("America/New_York", "30 1 * * *", "2026-11-01T04:00:00Z",
        "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z")]
    [InlineData("Europe/London", "30 1 * * *", "2026-10-24T23:00:00Z",
        "2026-10-25T00:30:00Z", "2026-10-26T01:30:00Z")]
    [InlineData("Australia/Sydney", "30 2 * * *", "2026-04-04T14:00:00Z",
        "2026-04-04T15:30:00Z", "2026-04-05T16:30:00Z")]
    public void FallFoldFixedTimeFiresOnceAtEarlierOffsetEvenWhenCursorRestartsInsideFold(
        string zone, string cron, string after, string earlier, string followingDay)
    {
        var schedule = ScheduleExpression.Create(cron, null, zone);
        Assert.Equal([Instant(earlier), Instant(followingDay)], schedule.GetNextOccurrences(Instant(after), 2));
        Assert.Equal(Instant(followingDay), schedule.GetNextOccurrence(Instant(earlier).AddMinutes(15)));
        Assert.Equal(Instant(followingDay), schedule.GetNextOccurrence(Instant(earlier).AddMinutes(45)));
    }

    [Theory]
    [InlineData("America/New_York", "*/30 1 * * *", "2026-11-01T04:59:59.9999999Z",
        "2026-11-01T05:00:00Z", "2026-11-01T05:30:00Z", "2026-11-01T06:00:00Z", "2026-11-01T06:30:00Z")]
    [InlineData("Europe/London", "*/30 1 * * *", "2026-10-24T23:59:59.9999999Z",
        "2026-10-25T00:00:00Z", "2026-10-25T00:30:00Z", "2026-10-25T01:00:00Z", "2026-10-25T01:30:00Z")]
    [InlineData("Australia/Sydney", "*/30 2 * * *", "2026-04-04T14:59:59.9999999Z",
        "2026-04-04T15:00:00Z", "2026-04-04T15:30:00Z", "2026-04-04T16:00:00Z", "2026-04-04T16:30:00Z")]
    public void FallFoldIntervalLikeCronPreservesBothOffsetsWithoutRepeatingAnInstant(
        string zone, string cron, string after, string first, string second, string third, string fourth)
    {
        var schedule = ScheduleExpression.Create(cron, null, zone);
        var expected = new[] { Instant(first), Instant(second), Instant(third), Instant(fourth) };
        Assert.Equal(expected, schedule.GetNextOccurrences(Instant(after), 4));
        Assert.Equal(expected.Skip(2), schedule.GetNextOccurrences(Instant(second), 2));
    }

    [Theory]
    [InlineData("Etc/UTC")]
    [InlineData("America/New_York")]
    [InlineData("Europe/London")]
    [InlineData("Australia/Sydney")]
    public void IntervalAddsElapsedUtcDurationIncludingAcrossOffsetAndDateBoundaries(string zone)
    {
        var schedule = ScheduleExpression.Create(null, TimeSpan.FromHours(1), zone);
        Assert.Null(schedule.Cron);
        Assert.Equal(TimeSpan.FromHours(1), schedule.Interval);
        Assert.Equal(Instant("2026-03-08T07:30:00Z"),
            schedule.GetNextOccurrence(Instant("2026-03-08T01:30:00-05:00")));
        Assert.Equal(Instant("2026-11-01T06:30:00Z"),
            schedule.GetNextOccurrence(Instant("2026-11-01T01:30:00-04:00")));
        Assert.Equal(Instant("2027-01-01T00:30:00Z"),
            schedule.GetNextOccurrence(Instant("2026-12-31T23:30:00Z")));
    }

    [Fact]
    public void IntervalSupportsSingleTicksAndRangeExhaustionWithoutOverflow()
    {
        var schedule = ScheduleExpression.Create(null, TimeSpan.FromTicks(1), "Etc/UTC");
        Assert.Equal(DateTimeOffset.MinValue.AddTicks(1), schedule.GetNextOccurrence(DateTimeOffset.MinValue));
        Assert.Equal(DateTimeOffset.MaxValue, schedule.GetNextOccurrence(DateTimeOffset.MaxValue.AddTicks(-1)));
        Assert.Null(schedule.GetNextOccurrence(DateTimeOffset.MaxValue));
        Assert.Null(ScheduleExpression.Create(null, TimeSpan.MaxValue, "Etc/UTC")
            .GetNextOccurrence(DateTimeOffset.MinValue));
        Assert.Equal([DateTimeOffset.MaxValue],
            schedule.GetNextOccurrences(DateTimeOffset.MaxValue.AddTicks(-1)));
    }

    [Fact]
    public void UnreachableCronReturnsNoOccurrences()
    {
        var schedule = ScheduleExpression.Create("0 0 30 2 *", null, "Etc/UTC");
        Assert.Null(schedule.GetNextOccurrence(Instant("2026-01-01T00:00:00Z")));
        Assert.Empty(schedule.GetNextOccurrences(Instant("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void CronUtcRangeExhaustionDoesNotRoundPastMaximumDate()
    {
        var schedule = ScheduleExpression.Create("* * * * *", null, "Etc/UTC");
        var lastMinute = Instant("9999-12-31T23:59:00Z");
        Assert.Equal(lastMinute, schedule.GetNextOccurrence(lastMinute.AddTicks(-1)));
        Assert.Null(schedule.GetNextOccurrence(lastMinute));
        Assert.Null(schedule.GetNextOccurrence(DateTimeOffset.MaxValue));
        Assert.Equal([lastMinute], schedule.GetNextOccurrences(lastMinute.AddTicks(-1)));
    }

    [Fact]
    public void EverySecondMacroCanReachLastWholeSecondWithoutOverflow()
    {
        var schedule = ScheduleExpression.Create("@every_second", null, "Etc/UTC");
        var lastSecond = Instant("9999-12-31T23:59:59Z");
        Assert.Equal(lastSecond, schedule.GetNextOccurrence(lastSecond.AddTicks(-1)));
        Assert.Null(schedule.GetNextOccurrence(lastSecond));
        Assert.Null(schedule.GetNextOccurrence(lastSecond.AddTicks(1)));
    }

    [Fact]
    public void CronCursorOutsideTimezoneCalendarRangeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScheduleExpression.Create("* * * * *", null, "America/New_York")
                .GetNextOccurrence(DateTimeOffset.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScheduleExpression.Create("* * * * *", null, "Australia/Sydney")
                .GetNextOccurrence(DateTimeOffset.MaxValue.AddHours(-1)));
    }

    [Fact]
    public void CronNextInstantOutsideUtcRangeFailsExplicitly()
    {
        var schedule = ScheduleExpression.Create("0 23 * * *", null, "America/New_York");
        var error = Assert.Throws<InvalidOperationException>(() =>
            schedule.GetNextOccurrence(Instant("9999-12-31T20:00:00Z")));
        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        Assert.Contains("range", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDefaultsToFiveExclusiveUtcInstantsAndValidatesCount()
    {
        var schedule = ScheduleExpression.Create("* * * * *", null, "Etc/UTC");
        var cursor = Instant("2026-01-01T23:58:00+01:00");
        Assert.Equal([
            Instant("2026-01-01T22:59:00Z"), Instant("2026-01-01T23:00:00Z"),
            Instant("2026-01-01T23:01:00Z"), Instant("2026-01-01T23:02:00Z"),
            Instant("2026-01-01T23:03:00Z")
        ], schedule.GetNextOccurrences(cursor));
        Assert.Empty(schedule.GetNextOccurrences(cursor, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.GetNextOccurrences(cursor, -1));
    }

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
