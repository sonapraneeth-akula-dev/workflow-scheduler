using System.Globalization;
using System.Text.Json;
using Wf.Validation.Schedules;

namespace Wf.Integration.Tests.Schedules;

public sealed class ScheduleExpressionIntegrationTests
{
    [Theory]
    [InlineData("America/New_York", "*/30 1 * * *", "2026-11-01T04:59:59.9999999Z",
        "2026-11-01T05:00:00Z", "2026-11-01T05:30:00Z", "2026-11-01T06:00:00Z",
        "2026-11-01T06:30:00Z", "2026-11-02T06:00:00Z")]
    [InlineData("Europe/London", "*/30 1 * * *", "2026-10-24T23:59:59.9999999Z",
        "2026-10-25T00:00:00Z", "2026-10-25T00:30:00Z", "2026-10-25T01:00:00Z",
        "2026-10-25T01:30:00Z", "2026-10-26T01:00:00Z")]
    [InlineData("Australia/Sydney", "*/30 2 * * *", "2026-04-04T14:59:59.9999999Z",
        "2026-04-04T15:00:00Z", "2026-04-04T15:30:00Z", "2026-04-04T16:00:00Z",
        "2026-04-04T16:30:00Z", "2026-04-05T16:00:00Z")]
    public void PreviewAndRestartedCursorConsumerAgreeAcrossFallFold(
        string zone, string cron, string after, string first, string second, string third, string fourth, string fifth)
    {
        AssertPreviewAndCursor(new(cron, null, zone), after, [first, second, third, fourth, fifth]);
    }

    [Theory]
    [InlineData("America/New_York", "30 2 * * *", "2026-03-08T06:59:59.9999999Z",
        "2026-03-08T07:00:00Z", "2026-03-09T06:30:00Z", "2026-03-10T06:30:00Z",
        "2026-03-11T06:30:00Z", "2026-03-12T06:30:00Z")]
    [InlineData("Europe/London", "30 1 * * *", "2026-03-29T00:59:59.9999999Z",
        "2026-03-29T01:00:00Z", "2026-03-30T00:30:00Z", "2026-03-31T00:30:00Z",
        "2026-04-01T00:30:00Z", "2026-04-02T00:30:00Z")]
    [InlineData("Australia/Sydney", "30 2 * * *", "2026-10-03T15:59:59.9999999Z",
        "2026-10-03T16:00:00Z", "2026-10-04T15:30:00Z", "2026-10-05T15:30:00Z",
        "2026-10-06T15:30:00Z", "2026-10-07T15:30:00Z")]
    public void PreviewAndRestartedCursorConsumerAgreeAcrossSpringGap(
        string zone, string cron, string after, string first, string second, string third, string fourth, string fifth)
    {
        AssertPreviewAndCursor(new(cron, null, zone), after, [first, second, third, fourth, fifth]);
    }

    [Fact]
    public void IntervalCursorRoundTripPreservesSubsecondPrecisionAndUtcDuration()
    {
        AssertPreviewAndCursor(new(null, TimeSpan.FromTicks(1), "America/New_York"),
            "2026-11-01T01:59:59.9999998-04:00", [
                "2026-11-01T05:59:59.9999999Z", "2026-11-01T06:00:00.0000000Z",
                "2026-11-01T06:00:00.0000001Z", "2026-11-01T06:00:00.0000002Z",
                "2026-11-01T06:00:00.0000003Z"
            ]);
    }

    private static void AssertPreviewAndCursor(ScheduleInput input, string after, string[] expectedValues)
    {
        var configurationJson = JsonSerializer.Serialize(input);
        var cursorJson = JsonSerializer.Serialize(Instant(after));
        var expected = expectedValues.Select(Instant).ToArray();
        var preview = Restore().GetNextOccurrences(JsonSerializer.Deserialize<DateTimeOffset>(cursorJson));
        Assert.Equal(expected, preview);

        var consumed = new List<DateTimeOffset>();
        foreach (var expectedInstant in expected)
        {
            // Rehydrate configuration and last scheduled instant as a restarted leader would.
            var cursor = JsonSerializer.Deserialize<DateTimeOffset>(cursorJson);
            var next = Restore().GetNextOccurrence(cursor);
            Assert.NotNull(next);
            Assert.Equal(expectedInstant, next.Value);
            Assert.True(next.Value > cursor);
            Assert.Equal(TimeSpan.Zero, next.Value.Offset);
            consumed.Add(next.Value);
            cursorJson = JsonSerializer.Serialize(next.Value);
        }
        Assert.Equal(expected, consumed);
        Assert.Equal(expected.Length, consumed.Distinct().Count());
        Assert.Equal(expected.Skip(2), Restore().GetNextOccurrences(expected[1], 3));

        ScheduleExpression Restore()
        {
            var restored = JsonSerializer.Deserialize<ScheduleInput>(configurationJson)!;
            return ScheduleExpression.Create(restored.Cron, restored.Interval, restored.TimeZoneId);
        }
    }

    private sealed record ScheduleInput(string? Cron, TimeSpan? Interval, string TimeZoneId);

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
