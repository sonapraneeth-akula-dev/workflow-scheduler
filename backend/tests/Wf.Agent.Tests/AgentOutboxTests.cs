using Wf.Agent;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class AgentOutboxTests
{
    [Fact]
    public void Offline_result_reserves_a_positive_Hello_and_is_retained_until_acknowledged()
    {
        var outbox = new AgentOutbox();
        outbox.Enqueue(new AgentMessage { TaskStarted = new TaskStarted() });
        var hello = outbox.CreateHello(new Hello());
        Assert.Equal(1UL, hello.Sequence);
        var first = outbox.NextAfter(hello.Sequence)!;
        Assert.Equal(2UL, first.Sequence);
        first.TaskStarted.Task = new AttemptRef { TaskKey = "mutated" };
        Assert.Null(outbox.NextAfter(hello.Sequence)!.TaskStarted.Task);
        Assert.Equal(hello.Sequence, outbox.CreateHello(new Hello()).Sequence);
        outbox.Acknowledge(new Ack { Sequence = 1 });
        Assert.NotNull(outbox.NextAfter(1));
        outbox.Acknowledge(new Ack { Sequence = 2 });
        Assert.Null(outbox.NextAfter(1));
        Assert.Equal(3UL, outbox.CreateHello(new Hello()).Sequence);
    }

    [Fact]
    public void Cumulative_ack_leaves_later_results_in_original_order_on_reconnect()
    {
        var outbox = new AgentOutbox();
        Assert.Equal(1UL, outbox.CreateHello(new Hello()).Sequence);
        outbox.Enqueue(new AgentMessage { TaskStarted = new TaskStarted() });
        outbox.Enqueue(new AgentMessage { Heartbeat = new Heartbeat() });
        outbox.Enqueue(new AgentMessage { TaskCompleted = new TaskCompleted() });
        outbox.Acknowledge(new Ack { Sequence = 2 });
        var hello = outbox.CreateHello(new Hello());
        Assert.Equal(2UL, hello.Sequence);
        Assert.Equal(3UL, outbox.NextAfter(hello.Sequence)!.Sequence);
        Assert.Equal(4UL, outbox.NextAfter(3)!.Sequence);
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(2UL, 0UL)]
    [InlineData(1UL, 1UL)]
    public void Invalid_acknowledgements_are_rejected(ulong sequence, ulong epoch)
    {
        var outbox = new AgentOutbox();
        outbox.CreateHello(new Hello());
        Assert.Throws<InvalidDataException>(() => outbox.Acknowledge(new Ack { Sequence = sequence, LeaderEpoch = epoch }));
    }

    [Fact]
    public async Task Parallel_results_have_unique_ordered_sequences_and_signal_the_sender()
    {
        var outbox = new AgentOutbox();
        await Parallel.ForAsync(0, 100, TestContext.Current.CancellationToken, (_, _) =>
        {
            outbox.Enqueue(new AgentMessage { TaskCompleted = new TaskCompleted() });
            return ValueTask.CompletedTask;
        });
        await outbox.WaitForChangeAsync(TestContext.Current.CancellationToken);
        var sequence = outbox.CreateHello(new Hello()).Sequence;
        for (var i = 0; i < 100; i++)
        {
            var message = outbox.NextAfter(sequence);
            Assert.NotNull(message);
            Assert.Equal(sequence + 1, message.Sequence);
            sequence = message.Sequence;
        }
        Assert.Null(outbox.NextAfter(sequence));
    }
}
