namespace Wf.ControlPlane.Engine.Scheduler;

public sealed record ScheduleTimer(Guid ScheduleId, DateTimeOffset FireAt);

public sealed class ScheduleTimerHeap
{
    private PriorityQueue<ScheduleTimer, (long At, Guid Id)> timers = new();
    public int Count => timers.Count;
    public DateTimeOffset? NextFireAt => timers.TryPeek(out var next, out _) ? next.FireAt : null;

    public void Reset(IEnumerable<ScheduleTimer> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var replacement = new ScheduleTimerHeap();
        var ids = new HashSet<Guid>();
        foreach (var timer in snapshot)
        {
            if (!ids.Add(timer.ScheduleId)) throw new ArgumentException("A schedule must have only one cursor.", nameof(snapshot));
            replacement.Add(timer);
        }
        timers = replacement.timers;
    }

    public void Add(ScheduleTimer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        if (timer.ScheduleId == Guid.Empty) throw new ArgumentException("A schedule identity is required.", nameof(timer));
        timers.Enqueue(timer, (timer.FireAt.UtcTicks, timer.ScheduleId));
    }

    public bool TryTakeDue(DateTimeOffset now, out ScheduleTimer? timer)
    {
        if (timers.TryPeek(out var next, out _) && next.FireAt <= now)
        {
            timer = timers.Dequeue();
            return true;
        }
        timer = null;
        return false;
    }
}
