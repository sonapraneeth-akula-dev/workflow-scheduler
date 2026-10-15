using System.Collections.Immutable;
using Wf.Contracts.Schedules;

namespace Wf.Engine.Core.Scheduling;

public enum ScheduleFireOutcome { Started = 0, Skipped = 1, Missed = 2, CaughtUp = 3 }

public sealed record ScheduleFireDecision(DateTimeOffset FireAt, ScheduleFireOutcome Outcome);

// Separate run action: never insert a second schedule_fires row for SourceFireAt.
public sealed record CatchUpRun(DateTimeOffset SourceFireAt)
{
    public ScheduleFireOutcome Outcome => ScheduleFireOutcome.CaughtUp;
    public string Trigger => "catch-up";
}

public sealed record CatchUpPlan(ImmutableArray<ScheduleFireDecision> Fires, CatchUpRun? CatchUp);

public static class CatchUpPolicy
{
    public static CatchUpPlan Evaluate(IEnumerable<DateTimeOffset> dueFires, DateTimeOffset now, TimeSpan grace,
        ScheduleCatchUpPolicy policy = ScheduleCatchUpPolicy.Latest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dueFires);
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        cancellationToken.ThrowIfCancellationRequested();

        var fires = ImmutableArray.CreateBuilder<ScheduleFireDecision>();
        DateTimeOffset? previous = null;
        DateTimeOffset? latestMissed = null;
        foreach (var fire in dueFires)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var utc = fire.ToUniversalTime();
            if (utc > now || (previous is { } last && utc <= last))
                throw new ArgumentException("Due fires must be strictly increasing and not in the future.", nameof(dueFires));
            var missed = now - utc > grace;
            fires.Add(new(utc, missed ? ScheduleFireOutcome.Missed : ScheduleFireOutcome.Started));
            if (missed) latestMissed = utc;
            previous = utc;
        }
        return new(fires.ToImmutable(), policy == ScheduleCatchUpPolicy.Latest && latestMissed is { } source
            ? new CatchUpRun(source) : null);
    }
}
