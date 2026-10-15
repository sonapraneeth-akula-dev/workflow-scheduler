namespace Wf.Sdk.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private readonly HashSet<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        List<ManualTimer> due;
        lock (sync)
        {
            now += elapsed;
            due = timers.Where(t => t.At <= now).ToList();
            foreach (var timer in due)
                timer.At = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + timer.Period;
        }
        foreach (var timer in due)
            timer.Callback(timer.State);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset At { get; set; }
        public TimeSpan Period { get; private set; }
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync)
            {
                if (disposed)
                    return false;
                Period = period;
                At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
                owner.timers.Add(this);
                return true;
            }
        }
        public void Dispose()
        {
            lock (owner.sync)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
