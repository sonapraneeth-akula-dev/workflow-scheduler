using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts.Checkpoints;

namespace Wf.Sdk.Tests;

public sealed class CheckpointStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checkpoint_exact_one_MiB_is_accepted_and_one_extra_UTF8_byte_never_reaches_storage()
    {
        var count = 0;
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) =>
        {
            Assert.Equal(CheckpointProtocol.MaximumBytes, bytes.Length);
            CheckpointProtocol.ValidateState(bytes);
            count++;
            return Task.CompletedTask;
        });
        await checkpoint.SaveAsync(new string('a', CheckpointProtocol.MaximumBytes - 2), Token);
        Assert.Equal(1, count);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            checkpoint.SaveAsync(new string('a', CheckpointProtocol.MaximumBytes - 1), Token));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Checkpoint_second_and_concurrent_saves_wait_the_exact_five_second_interval()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<(long Sequence, DateTimeOffset At)>();
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) =>
        {
            calls.Add((sequence, at));
            return Task.CompletedTask;
        }, timeProvider: clock);
        await checkpoint.SaveAsync(1, Token);
        var second = checkpoint.SaveAsync(2, Token);
        var third = checkpoint.SaveAsync(3, Token);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.False(second.IsCompleted);
        Assert.Single(calls);
        clock.Advance(TimeSpan.FromTicks(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, checkpoint.TryLoad<int>());
        Assert.False(third.IsCompleted);
        // Let the queued save register its timer before advancing the manual clock.
        clock.Advance(TimeSpan.FromSeconds(5));
        await third.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal([1L, 2L, 3L], calls.Select(call => call.Sequence));
        Assert.Equal(TimeSpan.FromSeconds(5), calls[1].At - calls[0].At);
        Assert.Equal(TimeSpan.FromSeconds(5), calls[2].At - calls[1].At);
    }

    [Fact]
    public async Task Checkpoint_load_changes_only_after_durable_acknowledgement_and_retains_seed_until_then()
    {
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = Encoding.UTF8.GetBytes("""{"position":41}""");
        var clock = new ManualTimeProvider();
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) =>
        {
            Assert.Equal(8, sequence);
            return ack.Task.WaitAsync(token);
        }, initial, 7, clock.GetUtcNow().AddSeconds(-5), clock);
        var pending = checkpoint.SaveAsync(new CheckpointState(42), CheckpointTestJson.Default.CheckpointState, Token);
        Assert.False(pending.IsCompleted);
        Assert.Equal(41, checkpoint.TryLoad(CheckpointTestJson.Default.CheckpointState)!.Position);
        ack.SetResult();
        await pending;
        Assert.Equal(42, checkpoint.TryLoad(CheckpointTestJson.Default.CheckpointState)!.Position);
        initial[0] = (byte)'x';
        Assert.Equal(42, checkpoint.TryLoad(CheckpointTestJson.Default.CheckpointState)!.Position);
    }

    [Fact]
    public async Task Checkpoint_failed_save_never_reuses_a_possibly_committed_sequence_or_changes_loaded_state()
    {
        var clock = new ManualTimeProvider();
        var sequences = new List<long>();
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) =>
        {
            sequences.Add(sequence);
            return sequence == 1 ? Task.FromException(new IOException("injected lost acknowledgement")) : Task.CompletedTask;
        }, timeProvider: clock);
        await Assert.ThrowsAsync<IOException>(() => checkpoint.SaveAsync(1, Token));
        Assert.Equal(0, checkpoint.TryLoad<int>());
        var next = checkpoint.SaveAsync(2, Token);
        Assert.False(next.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        await next.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal([1L, 2L], sequences);
        Assert.Equal(2, checkpoint.TryLoad<int>());
    }

    [Fact]
    public async Task Checkpoint_forward_wall_clock_adjustment_cannot_bypass_the_monotonic_five_second_interval()
    {
        var clock = new ShiftableClock();
        var calls = 0;
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) =>
        {
            calls++;
            return Task.CompletedTask;
        }, timeProvider: clock);
        await checkpoint.SaveAsync(1, Token);
        clock.Offset = TimeSpan.FromSeconds(30);
        var pending = checkpoint.SaveAsync(2, Token);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, calls);
        clock.Monotonic.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.False(pending.IsCompleted);
        clock.Monotonic.Advance(TimeSpan.FromTicks(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Checkpoint_attempt_and_caller_cancellation_interrupt_interval_and_pending_ack()
    {
        var clock = new ManualTimeProvider();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var count = 0;
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) =>
        {
            count++;
            return Task.CompletedTask;
        }, timeProvider: clock, cancellationToken: attempt.Token);
        await checkpoint.SaveAsync(1, Token);
        using var caller = new CancellationTokenSource();
        var cancelled = checkpoint.SaveAsync(2, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, count);
        var stopped = checkpoint.SaveAsync(3, Token);
        attempt.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);
        Assert.Equal(1, checkpoint.TryLoad<int>());
    }

    [Fact]
    public async Task Checkpoint_offline_context_has_no_seed_and_never_pretends_a_save_is_durable()
    {
        var context = new TaskContext();
        Assert.Null(context.Checkpoint.TryLoad<string>());
        Assert.Equal(1u, context.Attempt.Number);
        Assert.Null(context.Attempt.ResumedFrom);
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Checkpoint.SaveAsync("state", Token));
    }

    [Fact]
    public async Task Checkpoint_context_task_clone_preserves_attempt_and_store_and_uses_explicit_source_generation()
    {
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) => Task.CompletedTask);
        var attempt = new TaskAttempt(2, DateTimeOffset.UtcNow.AddMinutes(-1));
        var workflow = Workflow.Define("tests/checkpoints/context", "1", w => w.Type("tests")
            .Node("work", n => n.Task("task", async context =>
            {
                Assert.Same(checkpoint, context.Checkpoint);
                Assert.Same(attempt, context.Attempt);
                await context.Checkpoint.SaveAsync(new CheckpointState(9), CheckpointTestJson.Default.CheckpointState,
                    context.CancellationToken);
            })));
        await new TaskRegistry(workflow).ExecuteAsync("work/task", new(Token, checkpoint: checkpoint, attempt: attempt));
        Assert.Equal(9, checkpoint.TryLoad(CheckpointTestJson.Default.CheckpointState)!.Position);
        await Assert.ThrowsAsync<NotSupportedException>(() => checkpoint.SaveAsync(new CheckpointState(10), Token));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(-1, false, false)]
    public void Checkpoint_seed_requires_a_complete_valid_state_reference(long sequence, bool emptyState, bool emptyTime)
    {
        Assert.Throws<ArgumentException>(() => new CheckpointStore(initialState: emptyState ? default : "{}"u8.ToArray(),
            sequence: sequence, savedAt: emptyTime ? null : DateTimeOffset.UtcNow));
    }

    private sealed class ShiftableClock : TimeProvider
    {
        public ManualTimeProvider Monotonic { get; } = new();
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => Monotonic.GetUtcNow() + Offset;
        public override long GetTimestamp() => Monotonic.GetTimestamp();
        public override long TimestampFrequency => Monotonic.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Monotonic.CreateTimer(callback, state, dueTime, period);
    }
}

public sealed record CheckpointState([property: JsonPropertyName("position")] int Position);

[JsonSerializable(typeof(CheckpointState))]
internal partial class CheckpointTestJson : JsonSerializerContext;
