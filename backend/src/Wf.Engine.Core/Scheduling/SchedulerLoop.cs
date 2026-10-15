using Wf.Contracts.Schedules;

namespace Wf.Engine.Core.Scheduling;

public sealed record SchedulerPlan(CatchUpPlan CatchUp, DateTimeOffset? NextFireAt);

/// <summary>Plans due slots from the persisted intended-fire cursor, without performing I/O.</summary>
public sealed class SchedulerLoop(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public SchedulerPlan Plan(DateTimeOffset? nextFireAt, Func<DateTimeOffset, DateTimeOffset?> nextOccurrence,
        TimeSpan grace, ScheduleCatchUpPolicy policy = ScheduleCatchUpPolicy.Latest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nextOccurrence);
        var now = _timeProvider.GetUtcNow();
        var next = nextFireAt?.ToUniversalTime();
        var plan = CatchUpPolicy.Evaluate(DueFires(), now, grace, policy, cancellationToken);
        return new(plan, next);

        IEnumerable<DateTimeOffset> DueFires()
        {
            while (next is { } fire && fire <= now)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return fire;
                next = nextOccurrence(fire)?.ToUniversalTime();
                if (next is { } following && following <= fire)
                    throw new InvalidOperationException("The schedule must advance strictly beyond its intended fire.");
            }
        }
    }
}
