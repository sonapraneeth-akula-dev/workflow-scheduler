using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;

namespace Wf.Contracts.Tests;

public sealed class CheckpointProtocolTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("run")]
    [InlineData("node")]
    [InlineData("task")]
    [InlineData("same-attempt")]
    [InlineData("newer-attempt")]
    [InlineData("attempt-overflow")]
    [InlineData("bytes")]
    [InlineData("size")]
    [InlineData("json")]
    public void Checkpoint_seed_must_be_verified_JSON_for_the_same_logical_task_and_an_earlier_attempt(string kind)
    {
        var saved = Saved();
        var bytes = kind == "json" ? ByteString.CopyFromUtf8("}{") : ByteString.CopyFromUtf8("{}");
        saved.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        saved.BlobKey = CheckpointProtocol.Key(Guid.Parse(saved.Task.RunId), "work/task", 1, saved.Sha256);
        var seed = new CheckpointSeed { Reference = saved, StateJson = bytes };
        var target = saved.Task.Clone();
        target.Attempt = 2;
        switch (kind)
        {
            case "run": target.RunId = Guid.CreateVersion7().ToString("D"); break;
            case "node": target.NodeKey = "other"; break;
            case "task": target.TaskKey = "other"; break;
            case "same-attempt": target.Attempt = 1; break;
            case "newer-attempt": saved.Task.Attempt = 3; break;
            case "attempt-overflow": target.Attempt = 32768; break;
            case "bytes": seed.StateJson = ByteString.CopyFromUtf8("[]"); break;
            case "size": seed.StateJson = ByteString.CopyFromUtf8(" {}"); break;
        }
        if (kind == "valid") Assert.Equal(1, CheckpointProtocol.ValidateSeed(seed, target).Sequence);
        else if (kind == "json") Assert.ThrowsAny<JsonException>(() => CheckpointProtocol.ValidateSeed(seed, target));
        else Assert.Throws<ArgumentException>(() => CheckpointProtocol.ValidateSeed(seed, target));
    }

    [Fact]
    public void Checkpoint_reference_roundtrips_and_uses_a_canonical_run_scoped_key_and_microsecond_time()
    {
        var saved = Saved();
        var reference = CheckpointProtocol.Validate(CheckpointSaved.Parser.ParseFrom(saved.ToByteArray()));
        Assert.Equal(Guid.Parse(saved.Task.RunId), reference.RunId);
        Assert.Equal("work/task", reference.TaskKey);
        Assert.StartsWith($"checkpoints/{saved.Task.RunId}/", reference.BlobKey);
        Assert.EndsWith("/1-" + saved.Sha256 + ".json", reference.BlobKey);
        Assert.Equal(0, reference.SavedAt.UtcTicks % 10);
        Assert.Equal(saved.SavedAt.ToDateTimeOffset().UtcTicks / 10 * 10, reference.SavedAt.UtcTicks);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run-version")]
    [InlineData("run-case")]
    [InlineData("node")]
    [InlineData("task")]
    [InlineData("control")]
    [InlineData("long-key")]
    [InlineData("attempt")]
    [InlineData("attempt-overflow")]
    [InlineData("epoch")]
    [InlineData("epoch-overflow")]
    [InlineData("sequence")]
    [InlineData("sequence-overflow")]
    [InlineData("size")]
    [InlineData("size-overflow")]
    [InlineData("sha")]
    [InlineData("sha-case")]
    [InlineData("path")]
    [InlineData("time")]
    [InlineData("invalid-time")]
    [InlineData("old-time")]
    public void Invalid_checkpoint_references_are_rejected_before_storage(string invalid)
    {
        var saved = Saved();
        switch (invalid)
        {
            case "run": saved.Task.RunId = "invalid"; break;
            case "run-version": saved.Task.RunId = Guid.NewGuid().ToString("D"); break;
            case "run-case": saved.Task.RunId = saved.Task.RunId.ToUpperInvariant(); break;
            case "node": saved.Task.NodeKey = "../work"; break;
            case "task": saved.Task.TaskKey = ""; break;
            case "control": saved.Task.TaskKey = "bad\nkey"; break;
            case "long-key": saved.Task.TaskKey = new string('a', 513); break;
            case "attempt": saved.Task.Attempt = 0; break;
            case "attempt-overflow": saved.Task.Attempt = 32768; break;
            case "epoch": saved.ReservationEpoch = 0; break;
            case "epoch-overflow": saved.ReservationEpoch = ulong.MaxValue; break;
            case "sequence": saved.CheckpointSequence = 0; break;
            case "sequence-overflow": saved.CheckpointSequence = ulong.MaxValue; break;
            case "size": saved.SizeBytes = 0; break;
            case "size-overflow": saved.SizeBytes = CheckpointProtocol.MaximumBytes + 1UL; break;
            case "sha": saved.Sha256 = "invalid"; break;
            case "sha-case": saved.Sha256 = new string('A', 64); break;
            case "path": saved.BlobKey = "checkpoints/../../outside"; break;
            case "time": saved.SavedAt = null; break;
            case "invalid-time": saved.SavedAt.Nanos = -1; break;
            case "old-time": saved.SavedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddSeconds(-1)); break;
        }
        Assert.Throws<ArgumentException>(() => CheckpointProtocol.Validate(saved));
    }

    [Fact]
    public void Exactly_one_MiB_of_JSON_is_accepted_but_one_more_byte_and_invalid_JSON_are_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes("\"" + new string('a', CheckpointProtocol.MaximumBytes - 2) + "\"");
        CheckpointProtocol.ValidateState(bytes);
        Assert.Throws<ArgumentException>(() => CheckpointProtocol.ValidateState(new byte[CheckpointProtocol.MaximumBytes + 1]));
        Assert.Throws<ArgumentException>(() => CheckpointProtocol.ValidateState(ReadOnlyMemory<byte>.Empty));
        Assert.ThrowsAny<JsonException>(() => CheckpointProtocol.ValidateState("{"u8.ToArray()));
    }

    private static CheckpointSaved Saved()
    {
        var run = Guid.Parse("0199ea00-0000-7000-8000-000000000001");
        var sha = new string('a', 64);
        return new()
        {
            Task = new() { RunId = run.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
            ReservationEpoch = 1, CheckpointSequence = 1,
            SavedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero).AddTicks(7)),
            BlobKey = CheckpointProtocol.Key(run, "work/task", 1, sha), Sha256 = sha, SizeBytes = 2
        };
    }
}
