using Google.Protobuf;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using Google.Protobuf.WellKnownTypes;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class ResultJournalTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(".agent-artifacts", "journal-tests", Guid.NewGuid().ToString("N")));
    private readonly string id = Guid.NewGuid().ToString();
    private AgentOutbox Open() => new(new ResultJournal(root, id));

    [Fact]
    public void Newly_created_journal_directory_is_private_to_its_agent_account()
    {
        using var outbox = Open();
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectoryInfo(root).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(identity.User, rule.IdentityReference);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        }
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
    }

    [Fact]
    public void New_objects_replay_original_bytes_until_ack_then_keep_the_sequence_high_watermark()
    {
        var completed = Result();
        byte[] original;
        using (var first = Open())
        {
            first.Enqueue(new AgentMessage { TaskStarted = new TaskStarted { Task = completed.TaskCompleted.Task.Clone(), At = completed.TaskCompleted.At.Clone() } });
            first.Enqueue(completed);
            original = first.NextAfter(2)!.ToByteArray();
            first.ObserveLeaderEpoch(9);
        }
        using (var restarted = Open())
        {
            Assert.Equal(1UL, restarted.CreateHello(new Hello()).Sequence);
            Assert.Equal(original, restarted.NextAfter(2)!.ToByteArray());
            Assert.True(restarted.ContainsResult(completed.TaskCompleted.Task));
            Assert.Throws<InvalidDataException>(() => restarted.ObserveLeaderEpoch(8));
            restarted.Acknowledge(new Ack { Sequence = 2 });
            Assert.Equal(original, restarted.NextAfter(0)!.ToByteArray());
        }
        using (var restartedAgain = Open())
        {
            Assert.Equal(2UL, restartedAgain.CreateHello(new Hello()).Sequence);
            restartedAgain.Acknowledge(new Ack { Sequence = 3 });
        }
        using var empty = Open();
        Assert.Null(empty.NextAfter(0));
        Assert.Equal(4UL, empty.CreateHello(new Hello()).Sequence);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("node")]
    [InlineData("attempt")]
    [InlineData("timestamp")]
    [InlineData("outcome")]
    [InlineData("json")]
    [InlineData("json-shape")]
    [InlineData("outputs-size")]
    [InlineData("error-size")]
    [InlineData("record-size")]
    public void Invalid_results_never_change_the_durable_journal(string field)
    {
        using var outbox = Open();
        var before = File.ReadAllBytes(Path.Combine(root, "results.wfj"));
        var message = Result();
        var result = message.TaskCompleted;
        switch (field)
        {
            case "identity": result.Task.RunId = "../invalid"; break;
            case "node": result.Task.NodeKey = "node/task"; break;
            case "attempt": result.Task.Attempt = 32768; break;
            case "timestamp": result.At = null; break;
            case "outcome": result.Outcome = (TaskOutcome)99; break;
            case "json": result.OutputsJson = ByteString.CopyFromUtf8("{"); break;
            case "json-shape": result.OutputsJson = ByteString.CopyFromUtf8("[]"); break;
            case "outputs-size": result.OutputsJson = ByteString.CopyFrom(new byte[65537]); break;
            case "error-size": result.Error = new TaskError { Message = new string('é', 2049) }; break;
            case "record-size": result.Error = new TaskError { Category = new string('a', ResultJournal.MaximumRecordBytes) }; break;
        }
        Assert.Throws<InvalidDataException>(() => outbox.Enqueue(message));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "results.wfj")));
        Assert.Null(outbox.NextAfter(0));
    }

    [Fact]
    public void Failed_atomic_publish_retains_both_existing_memory_and_disk_without_consuming_a_sequence()
    {
        byte[] original;
        using (var outbox = Open())
        {
            outbox.Enqueue(Result());
            original = File.ReadAllBytes(Path.Combine(root, "results.wfj"));
            using var blocked = new FileStream(Path.Combine(root, "results.wfj.next"), FileMode.Create, FileAccess.Write, FileShare.None);
            Assert.ThrowsAny<IOException>(() => outbox.Enqueue(Result()));
            Assert.Equal(2UL, outbox.NextAfter(0)!.Sequence);
            Assert.Null(outbox.NextAfter(2));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "results.wfj")));
            Assert.ThrowsAny<IOException>(() => outbox.Acknowledge(new Ack { Sequence = 2 }));
            Assert.NotNull(outbox.NextAfter(0));
            blocked.Dispose();
            File.Delete(Path.Combine(root, "results.wfj.next"));
        }
        using var restored = Open();
        Assert.Equal(2UL, restored.NextAfter(0)!.Sequence);
        restored.Enqueue(Result());
        Assert.Equal(3UL, restored.NextAfter(2)!.Sequence);
    }

    [Fact]
    public void Multiple_writers_and_foreign_agent_ids_are_rejected_without_overwriting_results()
    {
        using (var owner = Open())
        {
            owner.Enqueue(Result());
            Assert.Throws<IOException>(() => Open());
        }
        var original = File.ReadAllBytes(Path.Combine(root, "results.wfj"));
        Assert.Throws<InvalidDataException>(() => new ResultJournal(root, Guid.NewGuid().ToString()));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "results.wfj")));
        using var restored = Open();
        Assert.NotNull(restored.NextAfter(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Corrupt_or_truncated_snapshots_fail_closed(bool truncate)
    {
        using (var outbox = Open()) outbox.Enqueue(Result());
        var file = Path.Combine(root, "results.wfj");
        var bytes = File.ReadAllBytes(file);
        if (truncate) bytes = bytes[..^1];
        else bytes[0] ^= 1;
        File.WriteAllBytes(file, bytes);
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("length")]
    [InlineData("sequence")]
    [InlineData("reservationEpoch")]
    public void Structurally_invalid_records_are_rejected_even_with_a_valid_checksum(string field)
    {
        using (var outbox = Open()) outbox.Enqueue(Result());
        var file = Path.Combine(root, "results.wfj");
        var bytes = File.ReadAllBytes(file);
        if (field == "count") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), ResultJournal.MaximumRecords + 1);
        if (field == "length") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), ResultJournal.MaximumRecordBytes + 1);
        if (field == "sequence") bytes[53] = 1;
        if (field == "reservationEpoch") BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(36), 0);
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
        File.WriteAllBytes(file, bytes);
        Assert.Throws<InvalidDataException>(() => { using var opened = Open(); });
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void Oversized_snapshot_is_rejected_before_allocating_its_contents()
    {
        using (var outbox = Open()) outbox.Enqueue(Result());
        using (var file = File.OpenWrite(Path.Combine(root, "results.wfj")))
            file.SetLength(ResultJournal.MaximumJournalBytes + 1L);
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [Fact]
    public void Dangling_snapshot_symlink_cannot_redirect_a_result_write()
    {
        using var outbox = Open();
        var target = Path.Combine(root, "must-not-be-created");
        try { File.CreateSymbolicLink(Path.Combine(root, "results.wfj.next"), target); }
        catch (Exception exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Skip("Symbolic-link creation requires Windows Developer Mode or SeCreateSymbolicLinkPrivilege.");
        }
        var original = File.ReadAllBytes(Path.Combine(root, "results.wfj"));
        Assert.Throws<IOException>(() => outbox.Enqueue(Result()));
        Assert.False(File.Exists(target));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "results.wfj")));
        Assert.Null(outbox.NextAfter(0));
    }

    [Fact]
    public void Heartbeats_do_not_fill_the_durable_result_journal_but_sequence_allocations_survive_restart()
    {
        using (var outbox = Open())
        {
            outbox.Enqueue(Result());
            for (var i = 0; i < 20; i++) outbox.Enqueue(new AgentMessage { Heartbeat = new Heartbeat() });
            Assert.Equal(22UL, outbox.NextAfter(2)!.Sequence);
            Assert.Null(outbox.NextAfter(22));
        }
        using var restored = Open();
        Assert.Equal(2UL, restored.NextAfter(0)!.Sequence);
        Assert.Null(restored.NextAfter(2));
        restored.Enqueue(Result());
        Assert.Equal(23UL, restored.NextAfter(2)!.Sequence);
    }

    [Fact]
    public void Unpublished_next_file_is_ignored_and_invalid_ack_cannot_remove_a_durable_result()
    {
        using (var outbox = Open()) outbox.Enqueue(Result());
        var file = Path.Combine(root, "results.wfj");
        var bytes = File.ReadAllBytes(file);
        File.WriteAllText(file + ".next", "interrupted snapshot");
        using var restored = Open();
        Assert.Throws<InvalidDataException>(() => restored.Acknowledge(new Ack { Sequence = 3 }));
        Assert.Throws<InvalidDataException>(() => restored.Acknowledge(new Ack { Sequence = 2, LeaderEpoch = 1 }));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.NotNull(restored.NextAfter(0));
    }

    private static AgentMessage Result() => new()
    {
        TaskCompleted = new TaskCompleted
        {
            Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 },
            At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Outcome = TaskOutcome.Succeeded,
            OutputsJson = ByteString.CopyFromUtf8("""{"result":42}""")
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
