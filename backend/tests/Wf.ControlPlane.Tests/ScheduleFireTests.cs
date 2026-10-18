using Wf.ControlPlane.Engine.Scheduler;

namespace Wf.ControlPlane.Tests;

public sealed class ScheduleFireTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Heap_orders_one_thousand_scrambled_cursors_by_intended_UTC_instant()
    {
        var heap = new ScheduleTimerHeap();
        var snapshot = Enumerable.Range(0, 1000).Select(index =>
            new ScheduleTimer(Guid.CreateVersion7(), Now.AddSeconds(index))).Reverse().ToArray();
        heap.Reset(snapshot);
        Assert.Equal(1000, heap.Count);
        for (var index = 0; index < 1000; index++)
        {
            Assert.Equal(Now.AddSeconds(index), heap.NextFireAt);
            Assert.True(heap.TryTakeDue(Now.AddSeconds(index), out var next));
            Assert.Equal(snapshot[999 - index], next);
        }
        Assert.Null(heap.NextFireAt);
        Assert.False(heap.TryTakeDue(DateTimeOffset.MaxValue, out var empty));
        Assert.Null(empty);
    }

    [Fact]
    public void Future_cursor_is_not_removed_until_exact_due_boundary()
    {
        var heap = new ScheduleTimerHeap();
        var timer = new ScheduleTimer(Guid.CreateVersion7(), Now.AddTicks(1));
        heap.Add(timer);
        Assert.False(heap.TryTakeDue(Now, out _));
        Assert.Equal(1, heap.Count);
        Assert.True(heap.TryTakeDue(timer.FireAt, out var due));
        Assert.Equal(timer, due);
    }

    [Fact]
    public void Equal_offset_equivalent_slots_use_deterministic_schedule_identity_tiebreaker()
    {
        var ids = Enumerable.Range(0, 30).Select(_ => Guid.CreateVersion7()).Order().ToArray();
        var heap = new ScheduleTimerHeap();
        heap.Reset(ids.Reverse().Select(id => new ScheduleTimer(id, Now.ToOffset(TimeSpan.FromHours(5.5)))));
        foreach (var id in ids)
        {
            Assert.True(heap.TryTakeDue(Now, out var due));
            Assert.Equal(id, due!.ScheduleId);
        }
    }

    [Fact]
    public void Reload_replaces_deleted_paused_and_edited_cursors_without_leaving_stale_entries()
    {
        var heap = new ScheduleTimerHeap();
        var removed = new ScheduleTimer(Guid.CreateVersion7(), Now);
        var edited = new ScheduleTimer(Guid.CreateVersion7(), Now);
        heap.Reset([removed, edited]);
        heap.Reset([edited with { FireAt = Now.AddMinutes(1) }]);
        Assert.False(heap.TryTakeDue(Now, out _));
        Assert.Equal(1, heap.Count);
        Assert.True(heap.TryTakeDue(Now.AddMinutes(1), out var due));
        Assert.Equal(edited.ScheduleId, due!.ScheduleId);
        heap.Reset([]);
        Assert.Equal(0, heap.Count);
    }

    [Fact]
    public void Invalid_reload_preserves_the_previous_complete_snapshot()
    {
        var heap = new ScheduleTimerHeap();
        var original = new ScheduleTimer(Guid.CreateVersion7(), Now);
        heap.Add(original);
        Assert.Throws<ArgumentException>(() => heap.Reset([original, original with { FireAt = Now.AddMinutes(1) }]));
        Assert.Throws<ArgumentException>(() => heap.Reset([new(Guid.Empty, Now)]));
        Assert.Throws<ArgumentNullException>(() => heap.Reset(null!));
        Assert.Throws<ArgumentNullException>(() => heap.Add(null!));
        Assert.Equal(1, heap.Count);
        Assert.True(heap.TryTakeDue(Now, out var due));
        Assert.Equal(original, due);
    }

    [Fact]
    public void Rearming_uses_the_persisted_intended_cursor_without_clock_drift()
    {
        var heap = new ScheduleTimerHeap();
        var timer = new ScheduleTimer(Guid.CreateVersion7(), Now.AddSeconds(-5));
        heap.Add(timer);
        Assert.True(heap.TryTakeDue(Now, out var due));
        heap.Add(due! with { FireAt = due.FireAt.AddMinutes(1) });
        Assert.Equal(timer.FireAt.AddMinutes(1), heap.NextFireAt);
        Assert.False(heap.TryTakeDue(Now, out _));
    }
}
