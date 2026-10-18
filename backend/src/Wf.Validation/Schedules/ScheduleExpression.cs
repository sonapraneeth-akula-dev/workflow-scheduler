using System.Collections.Immutable;
using Cronos;

namespace Wf.Validation.Schedules;

/// <summary>A validated cron or elapsed-duration schedule with an explicit IANA timezone.</summary>
public sealed class ScheduleExpression
{
    private static readonly DateTimeOffset LastCronSecond = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
    private readonly CronExpression? _expression;
    private readonly TimeZoneInfo _timeZone;

    private ScheduleExpression(string? cron, TimeSpan? interval, TimeZoneInfo timeZone, CronExpression? expression)
    {
        Cron = cron;
        Interval = interval;
        TimeZoneId = timeZone.Id;
        _timeZone = timeZone;
        _expression = expression;
    }

    public string? Cron { get; }
    public TimeSpan? Interval { get; }
    public string TimeZoneId { get; }

    /// <summary>
    /// Requires exactly one five-field Cronos expression (or deterministic macro) or positive interval,
    /// and a valid IANA timezone for either kind. Seconds-field expressions and seeded jitter are not supported.
    /// </summary>
    public static ScheduleExpression Create(string? cron, TimeSpan? interval, string timeZoneId)
    {
        if ((cron is null) == (interval is null))
            throw new ArgumentException("Specify exactly one cron expression or interval.");
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

        CronExpression? expression = null;
        if (cron is not null && !CronExpression.TryParse(cron, CronFormat.Standard, out expression))
            throw new ArgumentException("Invalid standard cron expression. Seconds fields and jitter are not supported.",
                nameof(cron));

        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out _))
            throw new ArgumentException("Timezone must be a recognized IANA identifier, not a Windows identifier.",
                nameof(timeZoneId));

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException("IANA timezone is not available on this system.", nameof(timeZoneId), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException("IANA timezone data is invalid on this system.", nameof(timeZoneId), exception);
        }
        return new(cron, interval, timeZone, expression);
    }

    /// <summary>
    /// Returns the first instant strictly after <paramref name="after"/>, normalized to UTC, or null if exhausted.
    /// An interval advances the supplied cursor by its elapsed duration; callers preserve intended-fire cursors,
    /// rather than substituting the current time. Cron uses Cronos DST semantics: missing wall times move forward;
    /// a fall fold repeats interval-like cron occurrences, but not fixed-time occurrences.
    /// A wildcard, range or step in the seconds, minutes or hours field makes a cron expression interval-like.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The cursor cannot be represented in the timezone calendar.</exception>
    /// <exception cref="InvalidOperationException">The next cron occurrence cannot be represented in UTC.</exception>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        var utc = after.ToUniversalTime();
        if (Interval is { } interval)
        {
            if (interval.Ticks > DateTimeOffset.MaxValue.UtcTicks - utc.UtcTicks)
                return null;
            return new DateTimeOffset(utc.UtcTicks + interval.Ticks, TimeSpan.Zero);
        }

        // Cronos rounds exclusive cursors upward; don't round beyond DateTime.MaxValue.
        if (utc >= LastCronSecond)
            return null;
        var localTicks = utc.UtcTicks + _timeZone.GetUtcOffset(utc).Ticks;
        if (localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(after), "Cursor is outside the timezone calendar range.");

        try
        {
            return _expression!.GetNextOccurrence(utc, _timeZone, inclusive: false)?.ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException("Next cron occurrence is outside the supported UTC date range.", exception);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> UTC instants, defaulting to the next five.
    /// Each returned instant is the exclusive cursor for the next; exhaustion may produce fewer results.
    /// </summary>
    public ImmutableArray<DateTimeOffset> GetNextOccurrences(DateTimeOffset after, int count = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var occurrences = ImmutableArray.CreateBuilder<DateTimeOffset>();
        for (var index = 0; index < count; index++)
        {
            if (GetNextOccurrence(after) is not { } next)
                break;
            occurrences.Add(next);
            after = next;
        }
        return occurrences.ToImmutable();
    }
}
