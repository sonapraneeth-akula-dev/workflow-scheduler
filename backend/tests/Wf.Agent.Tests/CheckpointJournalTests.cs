using Google.Protobuf.WellKnownTypes;
using Wf.Agent;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;

namespace Wf.Agent.Tests;

public sealed class CheckpointJournalTests
{
    [Fact]
    public void Checkpoint_pointer_survives_restarts_and_is_removed_only_by_the_cumulative_engine_ack()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-checkpoint-journal-" + Guid.NewGuid().ToString("N"));
        var agent = Guid.NewGuid().ToString("D");
        var saved = Saved();
        ulong sequence;
        try
        {
            using (var outbox = new AgentOutbox(new ResultJournal(root, agent)))
            {
                outbox.Enqueue(new() { CheckpointSaved = saved });
                sequence = outbox.NextAfter(1)!.Sequence;
                Assert.Equal(saved.Task, Assert.Single(outbox.UnacknowledgedAttempts()));
            }
            using (var outbox = new AgentOutbox(new ResultJournal(root, agent)))
            {
                var message = outbox.NextAfter(1)!;
                Assert.Equal(sequence, message.Sequence);
                Assert.Equal(saved, message.CheckpointSaved);
                Assert.False(outbox.ContainsResult(saved.Task));
                outbox.Acknowledge(new() { Sequence = sequence });
            }
            using var reopened = new AgentOutbox(new ResultJournal(root, agent));
            Assert.Null(reopened.NextAfter(1));
            Assert.Empty(reopened.UnacknowledgedAttempts());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_checkpoint_never_consumes_a_sequence_or_writes_a_journal_record()
    {
        using var outbox = new AgentOutbox();
        var saved = Saved();
        saved.BlobKey = "../other";
        Assert.Throws<ArgumentException>(() => outbox.Enqueue(new() { CheckpointSaved = saved }));
        Assert.Equal(1UL, outbox.CreateHello(new()).Sequence);
        Assert.Null(outbox.NextAfter(1));
    }

    private static CheckpointSaved Saved()
    {
        var run = Guid.CreateVersion7();
        var sha = new string('a', 64);
        return new()
        {
            Task = new() { RunId = run.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
            ReservationEpoch = 1, CheckpointSequence = 1, SavedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BlobKey = CheckpointProtocol.Key(run, "work/task", 1, sha), Sha256 = sha, SizeBytes = 2
        };
    }
}
