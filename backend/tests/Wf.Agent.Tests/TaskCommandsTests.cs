using System.Net;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class TaskCommandsTests
{
    [Fact]
    public async Task Dispatch_is_nonblocking_deduplicated_and_cancelled_while_downloading()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new PackageCacheTests.CacheFixture([], async (_, token) =>
        {
            began.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var dispatch = Dispatch(cache.Sha);
        await commands.HandleAsync(dispatch, TestContext.Current.CancellationToken);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(commands.Inflight);
        await commands.HandleAsync(dispatch.Clone(), TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.Handler.Count);
        var other = dispatch.Clone();
        other.Dispatch.Task.RunId = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await commands.HandleAsync(other, TestContext.Current.CancellationToken));
        await commands.HandleAsync(new ControlMessage { Cancel = new Cancel { Task = dispatch.Dispatch.Task.Clone() } },
            TestContext.Current.CancellationToken);
        var completed = await Completion(outbox);
        Assert.Equal(TaskOutcome.Cancelled, completed.Outcome);
        Assert.Equal("Aborted", completed.Error.Category);
        Assert.True(completed.Error.NonRetryable);
        await commands.DisposeAsync();
        Assert.Empty(commands.Inflight);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    [Fact]
    public async Task Package_failure_is_reported_explicitly_once_and_does_not_start_a_host()
    {
        await using var cache = new PackageCacheTests.CacheFixture([], (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var dispatch = Dispatch(cache.Sha);
        await commands.HandleAsync(dispatch, TestContext.Current.CancellationToken);
        var result = await Completion(outbox);
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal("PlatformError", result.Error.Category);
        Assert.Contains("404", result.Error.Message);
        Assert.False(result.Error.NonRetryable);
        await commands.HandleAsync(dispatch, TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.Handler.Count);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("deadline")]
    [InlineData("sha")]
    [InlineData("reservation")]
    public async Task Malformed_dispatch_cannot_start_or_enter_the_outbox(string field)
    {
        await using var cache = new PackageCacheTests.CacheFixture([]);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var message = Dispatch(cache.Sha);
        if (field == "identity") message.Dispatch.Task.RunId = "invalid";
        if (field == "deadline") message.Dispatch.DeadlineAt = null;
        if (field == "sha") message.Dispatch.Sha256 = "invalid";
        if (field == "reservation") message.Dispatch.ReservationEpoch = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await commands.HandleAsync(message, TestContext.Current.CancellationToken));
        Assert.Empty(commands.Inflight);
        Assert.Null(outbox.NextAfter(0));
        Assert.Equal(0, cache.Handler.Count);
    }

    private static TaskCommands Create(PackageCacheTests.CacheFixture cache, TaskHostPool hosts, AgentOutbox outbox) =>
        new(new AgentOptions([new Uri("http://localhost")], Guid.NewGuid().ToString(), "test", "ci", 1),
            cache.Cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);

    private static ControlMessage Dispatch(string sha) => new()
    {
        Dispatch = new Dispatch
        {
            Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "echo", Attempt = 1 },
            Version = "1", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30)),
            PackageUri = PackageCacheTests.CacheFixture.Url.ToString(), Sha256 = sha, ReservationEpoch = 1
        }
    };

    private static async Task<TaskCompleted> Completion(AgentOutbox outbox)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        ulong sequence = 0;
        while (true)
        {
            while (outbox.NextAfter(sequence) is { } message)
            {
                sequence = message.Sequence;
                if (message.TaskCompleted is not null) return message.TaskCompleted;
            }
            await outbox.WaitForChangeAsync(timeout.Token);
        }
    }
}
