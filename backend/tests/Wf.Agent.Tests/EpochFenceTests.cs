using System.Net;
using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class EpochFenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fence_is_persisted_before_cancel_and_acknowledged_only_after_older_execution_has_stopped()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new PackageCacheTests.CacheFixture([], async (_, token) =>
        {
            began.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.SetResult();
                await drained.Task.WaitAsync(Token);
                throw;
            }
            throw new InvalidOperationException("unreachable");
        });
        await using var hosts = Hosts();
        using var outbox = new AgentOutbox();
        await using var commands = Commands(cache, hosts, outbox);
        var dispatch = Dispatch(cache.Sha, 4);
        await commands.HandleAsync(dispatch, Token);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var fencing = commands.HandleAsync(new() { ReservationFence = new() { ReservationEpoch = 5 } }, Token).AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        try
        {
            Assert.Equal(5UL, outbox.ReservationEpoch);
            Assert.False(fencing.IsCompleted);
            Assert.DoesNotContain(Pending(outbox), message => message.ReservationFenced is not null);
            var stale = Dispatch(cache.Sha, 4);
            await commands.HandleAsync(stale, Token);
            Assert.Equal(1, cache.Handler.Count);
            var blocked = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await commands.HandleAsync(Dispatch(cache.Sha, 5), Token));
            Assert.Contains("physical reservation fencing", blocked.Message, StringComparison.Ordinal);
        }
        finally { drained.SetResult(); }
        await fencing.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var messages = Pending(outbox);
        Assert.Equal(4UL, Assert.Single(messages, message => message.TaskStarted is not null).TaskStarted.ReservationEpoch);
        var completed = Assert.Single(messages, message => message.TaskCompleted is not null).TaskCompleted;
        Assert.Equal(TaskOutcome.Cancelled, completed.Outcome);
        Assert.Equal(4UL, completed.ReservationEpoch);
        Assert.Equal(5UL, messages[^1].ReservationFenced.ReservationEpoch);
        Assert.Empty(commands.Inflight);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    [Fact]
    public async Task Durable_fence_and_unacknowledged_confirmation_survive_restart_and_block_old_dispatch()
    {
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "epoch-fence-tests", Guid.NewGuid().ToString("N")));
        var id = Guid.NewGuid().ToString();
        try
        {
            await using var cache = new PackageCacheTests.CacheFixture([], (_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
            await using var hosts = Hosts();
            using (var outbox = new AgentOutbox(new ResultJournal(root, id)))
            {
                await using var commands = Commands(cache, hosts, outbox);
                await commands.HandleAsync(new() { ReservationFence = new() { ReservationEpoch = 10 } }, Token);
                Assert.Equal(10UL, Assert.Single(Pending(outbox)).ReservationFenced.ReservationEpoch);
            }
            using (var restored = new AgentOutbox(new ResultJournal(root, id)))
            {
                await using var commands = Commands(cache, hosts, restored);
                Assert.Equal(10UL, restored.ReservationEpoch);
                Assert.Equal(10UL, Assert.Single(Pending(restored)).ReservationFenced.ReservationEpoch);
                await commands.HandleAsync(Dispatch(cache.Sha, 9), Token);
                Assert.Empty(commands.Inflight);
                Assert.Equal(0, cache.Handler.Count);
                restored.Acknowledge(new() { Sequence = Pending(restored)[^1].Sequence });
                await commands.HandleAsync(new() { ReservationFence = new() { ReservationEpoch = 10 } }, Token);
                Assert.Equal(10UL, Assert.Single(Pending(restored)).ReservationFenced.ReservationEpoch);
                await commands.HandleAsync(new() { ReservationFence = new() { ReservationEpoch = 8 } }, Token);
                Assert.Equal(10UL, restored.ReservationEpoch);
                Assert.Equal(10UL, Assert.Single(Pending(restored)).ReservationFenced.ReservationEpoch);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Legacy_WFJ1_is_read_without_discarding_sequence_and_upgraded_to_WFJ2_on_the_first_machine_fence()
    {
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "epoch-fence-tests", Guid.NewGuid().ToString("N")));
        var id = Guid.NewGuid();
        Directory.CreateDirectory(root);
        try
        {
            using var content = new MemoryStream();
            using (var writer = new BinaryWriter(content, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x314a4657u);
                writer.Write(id.ToByteArray());
                writer.Write(17UL);
                writer.Write(6UL);
                writer.Write(0);
            }
            var bytes = content.ToArray();
            File.WriteAllBytes(Path.Combine(root, "results.wfj"), [.. bytes, .. SHA256.HashData(bytes)]);
            using (var old = new AgentOutbox(new ResultJournal(root, id.ToString())))
            {
                Assert.Equal(1UL, old.ReservationEpoch);
                Assert.Equal(18UL, old.CreateHello(new Hello()).Sequence);
                old.ObserveLeaderEpoch(6);
                old.ObserveReservationEpoch(9);
                Assert.Throws<InvalidDataException>(() => old.ObserveReservationEpoch(8));
            }
            using var restored = new AgentOutbox(new ResultJournal(root, id.ToString()));
            Assert.Equal(9UL, restored.ReservationEpoch);
            Assert.Equal(19UL, restored.CreateHello(new Hello()).Sequence);
            Assert.Throws<InvalidDataException>(() => restored.ObserveLeaderEpoch(5));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_newer_dispatch_cannot_implicitly_fence_physically_active_older_work()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new PackageCacheTests.CacheFixture([], async (_, token) =>
        {
            began.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        await using var hosts = Hosts();
        using var outbox = new AgentOutbox();
        var options = new AgentOptions([new Uri("http://localhost")], Guid.NewGuid().ToString(), "epoch", "ci", 2);
        await using var commands = new TaskCommands(options, cache.Cache, hosts, outbox, TimeProvider.System,
            NullLogger<TaskCommands>.Instance);
        await commands.HandleAsync(Dispatch(cache.Sha, 3), Token);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await commands.HandleAsync(Dispatch(cache.Sha, 4), Token));
        Assert.Equal(3UL, outbox.ReservationEpoch);
        Assert.Single(commands.Inflight);
        await commands.HandleAsync(new() { ReservationFence = new() { ReservationEpoch = 2 } }, Token);
        await commands.HandleAsync(new() { ReservationFence = new() { ReservationEpoch = 3 } }, Token);
        Assert.Single(commands.Inflight);
        Assert.DoesNotContain(Pending(outbox), message => message.ReservationFenced is not null);
        await commands.HandleAsync(new() { ReservationFence = new() { ReservationEpoch = 4 } }, Token);
        Assert.Empty(commands.Inflight);
    }

    private static TaskCommands Commands(PackageCacheTests.CacheFixture cache, TaskHostPool hosts, AgentOutbox outbox) =>
        new(new AgentOptions([new Uri("http://localhost")], Guid.NewGuid().ToString(), "epoch", "ci", 1),
            cache.Cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);

    private static TaskHostPool Hosts() => new(NullLogger<TaskHostPool>.Instance,
        containmentDirectory: Path.Combine(AppContext.BaseDirectory, ".agent-artifacts", "epoch-fences", Guid.NewGuid().ToString("N")));

    private static ControlMessage Dispatch(string sha, ulong epoch) => new()
    {
        Dispatch = new()
        {
            Task = new() { RunId = Guid.NewGuid().ToString("D"), NodeKey = "work", TaskKey = "first", Attempt = 1 },
            Version = "1", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
            PackageUri = PackageCacheTests.CacheFixture.Url.ToString(), Sha256 = sha, ReservationEpoch = epoch
        }
    };

    private static List<AgentMessage> Pending(AgentOutbox outbox)
    {
        var messages = new List<AgentMessage>();
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message)
        {
            sequence = message.Sequence;
            messages.Add(message);
        }
        return messages;
    }
}
