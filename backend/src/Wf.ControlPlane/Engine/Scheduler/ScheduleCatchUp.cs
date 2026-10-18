using Wf.Contracts.Schedules;
using Wf.Engine.Core.Scheduling;
using Wf.Validation.Schedules;

namespace Wf.ControlPlane.Engine.Scheduler;

public sealed class ScheduleCatchUp
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(2);
    private readonly SchedulerLoop planner;
    public TimeSpan Grace { get; }

    public ScheduleCatchUp(TimeProvider time, TimeSpan? grace = null)
    {
        planner = new(time);
        Grace = grace ?? DefaultGrace;
        ArgumentOutOfRangeException.ThrowIfLessThan(Grace, TimeSpan.Zero, nameof(grace));
    }

    public SchedulerPlan Plan(DateTimeOffset nextFireAt, ScheduleExpression expression,
        ScheduleCatchUpPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return planner.Plan(nextFireAt, expression.GetNextOccurrence, Grace, policy, cancellationToken);
    }
}
