using System.Net;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class AgentDrainTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Drain_deadline_is_nonblocking_but_release_waits_for_physical_work_and_rejects_new_dispatch()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new PackageCacheTests.CacheFixture([], async (_, token) =>
        {
            began.TrySetResult();
            using var registration = token.Register(() => cancelled.TrySetResult());
            await release.Task.WaitAsync(Token);
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
            containmentDirectory: Path.Combine(cache.Root, "host-lifetimes"));
        using var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        await commands.HandleAsync(Dispatch(cache.Sha), Token);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var drain = Drain(expired: true);
        try
        {
            await commands.HandleAsync(drain, Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), Token);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await commands.HandleAsync(Dispatch(cache.Sha), Token);
            Assert.Single(commands.Inflight);
            Assert.Single(Messages(outbox));
            Assert.NotNull(Messages(outbox)[0].TaskStarted);
            Assert.Equal(1, cache.Handler.Count);
        }
        finally { release.TrySetResult(); }
        var result = await Drained(outbox);
        Assert.Equal(drain.Drain.Id, result.Id);
        Assert.Equal(1UL, result.ReservationEpoch);
        Assert.Empty(commands.Inflight);
        Assert.DoesNotContain(Messages(outbox), message => message.TaskCompleted is not null);
        await commands.HandleAsync(drain.Clone(), Token);
        Assert.Single(Messages(outbox), message => message.DrainCompleted is not null);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    [Fact]
    public async Task Drain_preserves_normal_results_and_does_not_cancel_work_before_the_deadline()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new PackageCacheTests.CacheFixture([], async (_, token) =>
        {
            began.TrySetResult();
            await finish.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
            containmentDirectory: Path.Combine(cache.Root, "host-lifetimes"));
        using var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        await commands.HandleAsync(Dispatch(cache.Sha), Token);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await commands.HandleAsync(Drain(expired: false), Token);
        Assert.Single(commands.Inflight);
        Assert.DoesNotContain(Messages(outbox), message => message.DrainCompleted is not null);
        finish.TrySetResult();
        await Drained(outbox);
        var messages = Messages(outbox);
        var completed = Assert.Single(messages, message => message.TaskCompleted is not null);
        Assert.Equal(TaskOutcome.Failed, completed.TaskCompleted.Outcome);
        Assert.Equal("PlatformError", completed.TaskCompleted.Error.Category);
        Assert.True(completed.Sequence < messages[^1].Sequence);
        Assert.NotNull(messages[^1].DrainCompleted);
    }

    [Fact]
    public async Task Drain_completion_survives_journal_restart_and_cannot_lower_the_machine_fence()
    {
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "drain-journal", Guid.NewGuid().ToString("N")));
        var agent = Guid.NewGuid().ToString("D");
        try
        {
            await using var cache = new PackageCacheTests.CacheFixture([]);
            await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
                containmentDirectory: Path.Combine(cache.Root, "host-lifetimes"));
            var drain = Drain(expired: false);
            ulong sequence;
            using (var original = new AgentOutbox(new ResultJournal(root, agent)))
            {
                await using var commands = Create(cache, hosts, original);
                original.ObserveReservationEpoch(2);
                await commands.HandleAsync(drain, Token);
                Assert.Empty(Messages(original));
                drain.Drain.ReservationEpoch = 2;
                await commands.HandleAsync(drain, Token);
                await Drained(original);
                sequence = Assert.Single(Messages(original)).Sequence;
            }
            using var restored = new AgentOutbox(new ResultJournal(root, agent));
            var result = Assert.Single(Messages(restored));
            Assert.Equal(sequence, result.Sequence);
            Assert.Equal(drain.Drain.Id, result.DrainCompleted.Id);
            Assert.Equal(2UL, result.DrainCompleted.ReservationEpoch);
            restored.Acknowledge(new() { Sequence = sequence });
            Assert.Empty(Messages(restored));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static TaskCommands Create(PackageCacheTests.CacheFixture cache, TaskHostPool hosts, AgentOutbox outbox) =>
        new(new AgentOptions([new Uri("http://localhost")], Guid.NewGuid().ToString("D"), "test", "ci", 1),
            cache.Cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);

    private static ControlMessage Drain(bool expired) => new()
    {
        Drain = new() { Id = Guid.CreateVersion7().ToString("D"), ReservationEpoch = 1, Reason = "Maintenance",
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 1)) }
    };

    private static ControlMessage Dispatch(string sha) => new()
    {
        Dispatch = new() { Task = new() { RunId = Guid.CreateVersion7().ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
            Version = "1", Sha256 = sha, PackageUri = PackageCacheTests.CacheFixture.Url.ToString(),
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)), ReservationEpoch = 1 }
    };

    private static List<AgentMessage> Messages(AgentOutbox outbox)
    {
        var messages = new List<AgentMessage>();
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message) { messages.Add(message); sequence = message.Sequence; }
        return messages;
    }

    private static async Task<DrainCompleted> Drained(AgentOutbox outbox)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (Messages(outbox).LastOrDefault()?.DrainCompleted is { } result) return result;
            await outbox.WaitForChangeAsync(timeout.Token);
        }
    }
}
