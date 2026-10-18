using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Logs;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;

namespace Wf.Agent.Tests;

public sealed class LiveLogMirrorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_watched_durably_accepted_masked_records_are_batched_at_250ms_without_journaling()
    {
        using var files = new LogFiles();
        using var outbox = new AgentOutbox();
        var clock = new FootprintClock();
        await using var mirror = new AgentLogMirror(outbox, clock, NullLogger<AgentLogMirror>.Instance);
        var task = LogSpoolTests.Attempt();
        await using var spool = new LogSpooler(files.Root, task, new SecretMasker(["sensitive-value"]), timeProvider: clock);
        spool.Committed += (line, frame) => mirror.Append(task, 1, line, frame);
        await spool.WriteAsync("stdout", "unwatched", cancellationToken: Token);
        clock.Advance(LiveLogProtocol.BatchInterval);
        Assert.Null(outbox.TakeLogs());
        mirror.Set(Watch(task, clock));
        await spool.WriteHostAsync(2, clock.GetUtcNow(), "stdout", "sensitive-value", Token);
        await spool.WriteHostAsync(2, clock.GetUtcNow(), "stdout", "duplicate", Token);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Null(outbox.TakeLogs());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await outbox.WaitForChangeAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        var message = Assert.IsType<AgentMessage>(outbox.TakeLogs());
        Assert.Equal(0UL, message.Sequence);
        var record = Assert.Single(message.LiveLogs.Records);
        Assert.Equal(2UL, record.Line);
        Assert.Equal("***", record.Text);
        Assert.False(message.LiveLogs.Gap);
        LiveLogProtocol.Validate(message.LiveLogs);
        Assert.Null(outbox.NextAfter(0));
        Assert.Equal(1UL, outbox.CreateHello(new()).Sequence);
    }

    [Fact]
    public async Task Off_expiry_epoch_advance_and_reconnect_clear_pending_bytes_and_previous_attempts_are_isolated()
    {
        using var outbox = new AgentOutbox();
        var clock = new FootprintClock();
        await using var mirror = new AgentLogMirror(outbox, clock, NullLogger<AgentLogMirror>.Instance);
        var task = LogSpoolTests.Attempt();
        var retry = task.Clone();
        retry.Attempt++;
        mirror.Set(Watch(task, clock));
        mirror.Append(retry, 1, 1, Frame(clock));
        mirror.Append(task, 2, 1, Frame(clock));
        mirror.Flush();
        Assert.Null(outbox.TakeLogs());
        mirror.Append(task, 1, 1, Frame(clock));
        mirror.Flush();
        var off = Watch(task, clock);
        off.On = false;
        mirror.Set(off);
        Assert.Null(outbox.TakeLogs());
        mirror.Set(Watch(task, clock));
        mirror.Append(task, 1, 2, Frame(clock));
        clock.Advance(LiveLogProtocol.InterestLifetime);
        mirror.Flush();
        Assert.Null(outbox.TakeLogs());
        mirror.Set(Watch(task, clock));
        mirror.Append(task, 1, 3, Frame(clock));
        outbox.ObserveReservationEpoch(2);
        mirror.Flush();
        Assert.Null(outbox.TakeLogs());
        Assert.Throws<InvalidDataException>(() => mirror.Set(Watch(task, clock)));
        var current = Watch(task, clock);
        current.ReservationEpoch = 2;
        mirror.Set(current);
        mirror.Append(task, 2, 4, Frame(clock));
        mirror.Flush();
        mirror.Reset();
        Assert.Null(outbox.TakeLogs());
        mirror.Append(task, 2, 5, Frame(clock));
        mirror.Flush();
        Assert.Null(outbox.TakeLogs());
    }

    [Fact]
    public async Task Backpressure_retains_bounded_records_and_reports_an_explicit_gap_after_overflow()
    {
        using var outbox = new AgentOutbox();
        var clock = new FootprintClock();
        await using var mirror = new AgentLogMirror(outbox, clock, NullLogger<AgentLogMirror>.Instance);
        var task = LogSpoolTests.Attempt();
        mirror.Set(Watch(task, clock));
        for (var line = 1; line <= 32; line++)
        {
            mirror.Append(task, 1, line, Frame(clock));
            mirror.Flush();
        }
        for (var line = 33; line <= 1000; line++)
            mirror.Append(task, 1, line, Frame(clock));
        mirror.Flush();
        for (var line = 1; line <= 32; line++)
            Assert.Equal((ulong)line, Assert.Single(Assert.IsType<AgentMessage>(outbox.TakeLogs()).LiveLogs.Records).Line);
        Assert.Null(outbox.TakeLogs());
        mirror.Flush();
        var batch = Assert.IsType<AgentMessage>(outbox.TakeLogs()).LiveLogs;
        LiveLogProtocol.Validate(batch);
        Assert.True(batch.Gap);
        Assert.Equal(256, batch.Records.Count);
        Assert.Equal(745UL, batch.Records[0].Line);
        Assert.Equal(1000UL, batch.Records[^1].Line);
    }

    [Fact]
    public async Task Multibyte_frames_respect_serialized_byte_ceiling_and_malformed_batches_are_rejected()
    {
        using var outbox = new AgentOutbox();
        var clock = new FootprintClock();
        await using var mirror = new AgentLogMirror(outbox, clock, NullLogger<AgentLogMirror>.Instance);
        var task = LogSpoolTests.Attempt();
        mirror.Set(Watch(task, clock));
        for (var line = 1; line <= 10; line++)
            mirror.Append(task, 1, line, Frame(clock) with { Text = new string('\u20ac', 64 * 1024) });
        mirror.Flush();
        var batch = Assert.IsType<AgentMessage>(outbox.TakeLogs()).LiveLogs;
        LiveLogProtocol.Validate(batch);
        Assert.True(batch.Gap);
        Assert.True(batch.CalculateSize() <= LiveLogProtocol.MaximumBatchBytes);
        Assert.Equal(10UL, batch.Records[^1].Line);
        batch.Records[^1].Line++;
        Assert.Throws<ArgumentException>(() => LiveLogProtocol.Validate(batch));
        Assert.Throws<ArgumentException>(() => LiveLogProtocol.Identity("invalid"));
        var expired = Watch(task, clock);
        expired.ExpiresAt = Timestamp.FromDateTimeOffset(clock.GetUtcNow());
        mirror.Set(expired);
        Assert.Null(outbox.TakeLogs());
        expired.ExpiresAt = Timestamp.FromDateTimeOffset(clock.GetUtcNow().AddSeconds(16));
        Assert.Throws<ArgumentException>(() => mirror.Set(expired));
    }

    [Fact]
    public async Task Delayed_expired_interest_clears_queued_bytes_without_interrupting_agent_commands()
    {
        using var outbox = new AgentOutbox();
        var clock = new FootprintClock();
        await using var mirror = new AgentLogMirror(outbox, clock, NullLogger<AgentLogMirror>.Instance);
        var task = LogSpoolTests.Attempt();
        var delayed = Watch(task, clock);
        mirror.Set(delayed);
        mirror.Append(task, 1, 1, Frame(clock));
        mirror.Flush();
        clock.Advance(LiveLogProtocol.InterestLifetime);
        mirror.Set(delayed);
        Assert.Null(outbox.TakeLogs());
        mirror.Append(task, 1, 2, Frame(clock));
        mirror.Flush();
        Assert.Null(outbox.TakeLogs());
        mirror.Set(Watch(task, clock));
        mirror.Append(task, 1, 3, Frame(clock));
        mirror.Flush();
        Assert.Equal(3UL, Assert.Single(Assert.IsType<AgentMessage>(outbox.TakeLogs()).LiveLogs.Records).Line);
        delayed.ReservationEpoch = 2;
        Assert.Throws<InvalidDataException>(() => mirror.Set(delayed));
    }

    private static MirrorLogs Watch(AttemptRef task, TimeProvider clock) => new()
    {
        Task = task.Clone(), On = true, ReservationEpoch = 1,
        ExpiresAt = Timestamp.FromDateTimeOffset(clock.GetUtcNow() + LiveLogProtocol.InterestLifetime)
    };

    private static LogFrame Frame(TimeProvider clock) => new(clock.GetUtcNow(), "stdout", "Information", "line");
}
