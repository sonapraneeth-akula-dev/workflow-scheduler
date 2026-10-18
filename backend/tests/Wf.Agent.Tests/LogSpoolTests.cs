using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Wf.Agent.Logs;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;

namespace Wf.Agent.Tests;

public sealed class LogSpoolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Host_record_replay_commits_cursor_with_the_whole_frame_and_never_duplicates_after_restart()
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        var at = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        string directory;
        await using (var spool = New(files, attempt))
        {
            directory = spool.DirectoryPath;
            await spool.WriteHostAsync(2, at, "stdout", "first", Token);
            Assert.Equal(2UL, (await spool.SnapshotAsync(Token)).HostRecordSequence);
        }
        // Simulate append fsync succeeding but atomic index replacement not committing.
        await File.AppendAllTextAsync(Path.Combine(directory, "active.raw"), "uncommitted-host-frame\n", Token);
        await using var recovered = New(files, attempt);
        await recovered.WriteHostAsync(2, at, "stdout", "first", Token);
        await recovered.WriteHostAsync(4, at.AddSeconds(1), "stderr", "second", Token);
        await recovered.CompleteAsync();
        await recovered.WriteHostAsync(4, at.AddSeconds(1), "stderr", "second", Token);
        var index = await recovered.SnapshotAsync(Token);
        Assert.Equal(4UL, index.HostRecordSequence);
        Assert.Equal(2, index.Lines);
        var text = Encoding.UTF8.GetString(ReadAll(recovered, index));
        Assert.DoesNotContain("uncommitted", text);
        Assert.Contains("\"text\":\"first\"", text);
        Assert.Contains("\"text\":\"second\"", text);
        Assert.Equal(at, JsonSerializer.Deserialize(text.Split('\n')[0], LogJsonContext.Default.LogFrame)!.At);
    }

    [Fact]
    public async Task Identity_is_durable_attempt_scoped_and_distinguishes_nodes_and_retries()
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        string directory;
        await using (var spool = New(files, attempt))
        {
            directory = spool.DirectoryPath;
            await spool.WriteAsync("stdout", "persisted", cancellationToken: Token);
            var index = await spool.SnapshotAsync(Token);
            Assert.Equal(LogIndex.PrefixFor(attempt), index.Prefix);
            Assert.Equal(attempt.NodeKey, index.NodeKey);
            Assert.Equal(attempt.TaskKey, index.TaskKey);
            Assert.Equal(attempt.Attempt, index.Attempt);
            Assert.Throws<IOException>(() => New(files, attempt));
        }
        await using var recovered = New(files, attempt);
        Assert.Equal(directory, recovered.DirectoryPath);
        Assert.Equal(1, (await recovered.SnapshotAsync(Token)).Lines);
        var sibling = attempt.Clone();
        sibling.NodeKey = "different";
        Assert.NotEqual(LogIndex.PrefixFor(attempt), LogIndex.PrefixFor(sibling));
        sibling.NodeKey = attempt.NodeKey;
        sibling.Attempt++;
        Assert.NotEqual(LogIndex.PrefixFor(attempt), LogIndex.PrefixFor(sibling));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("empty")]
    [InlineData("node")]
    [InlineData("task")]
    [InlineData("attempt")]
    [InlineData("large")]
    public void Invalid_identities_cannot_create_spool_files(string invalid)
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        switch (invalid)
        {
            case "run": attempt.RunId = "../escape"; break;
            case "empty": attempt.RunId = Guid.Empty.ToString(); break;
            case "node": attempt.NodeKey = "node/escape"; break;
            case "task": attempt.TaskKey = ""; break;
            case "attempt": attempt.Attempt = 0; break;
            case "large": attempt.TaskKey = new string('x', 513); break;
        }
        Assert.Throws<ArgumentException>(() => New(files, attempt));
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Exact_eight_MiB_boundary_seals_without_waiting_for_a_later_write()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        await using var spool = New(files, clock: clock);
        using var expected = new MemoryStream();
        await FillAsync(spool, clock, LogSpooler.ChunkBytes - 1, expected);
        Assert.Empty((await spool.SnapshotAsync(Token)).Chunks);
        var last = Frame(clock, "");
        expected.Write(last);
        await spool.WriteAsync("stdout", "", cancellationToken: Token);
        var first = Assert.Single((await spool.SnapshotAsync(Token)).Chunks);
        Assert.Equal(0, first.ByteStart);
        Assert.Equal(LogSpooler.ChunkBytes, first.ByteEnd);
        var gzip = File.ReadAllBytes(Path.Combine(spool.DirectoryPath, first.Name));
        Assert.Equal(new byte[] { 0x1f, 0x8b }, gzip[..2]);
        Assert.Equal(LogSpooler.ChunkBytes, Decompress(gzip).Length);
        await spool.CompleteAsync();
        var index = await spool.SnapshotAsync(Token);
        Assert.Equal(2, index.Chunks.Count);
        Assert.Equal(first.ByteEnd, index.Chunks[1].ByteStart);
        Assert.Equal(expected.ToArray(), ReadAll(spool, index));
    }

    [Fact]
    public async Task An_exactly_full_chunk_does_not_leave_an_empty_completion_chunk()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        await using var spool = New(files, clock: clock);
        await FillAsync(spool, clock, LogSpooler.ChunkBytes);
        Assert.Equal(LogSpooler.ChunkBytes, Assert.Single((await spool.SnapshotAsync(Token)).Chunks).ByteEnd);
        await spool.CompleteAsync();
        Assert.Single((await spool.SnapshotAsync(Token)).Chunks);
    }

    [Fact]
    public async Task Rotation_occurs_at_ten_seconds_from_first_byte_not_a_global_timer_tick()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        await using var spool = New(files, clock: clock);
        clock.Advance(TimeSpan.FromSeconds(7));
        var firstAt = clock.GetUtcNow();
        await spool.WriteAsync("stderr", "first", "Error", Token);
        clock.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.Empty((await spool.SnapshotAsync(Token)).Chunks);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await UntilAsync(async () => (await spool.SnapshotAsync(Token)).Chunks.Count == 1);
        var first = Assert.Single((await spool.SnapshotAsync(Token)).Chunks);
        Assert.Equal(firstAt, first.FirstAt);
        Assert.Equal(firstAt, first.LastAt);
        clock.Advance(TimeSpan.FromSeconds(123));
        Assert.Single((await spool.SnapshotAsync(Token)).Chunks);
        await spool.WriteAsync("stdout", "second", cancellationToken: Token);
        clock.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.Single((await spool.SnapshotAsync(Token)).Chunks);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await UntilAsync(async () => (await spool.SnapshotAsync(Token)).Chunks.Count == 2);
    }

    [Fact]
    public async Task Gzip_contains_structured_masked_frames_never_raw_or_base64_secrets()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        const string secret = "resolved-password";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        await using var spool = New(files, clock: clock, masker: new([secret]));
        await spool.WriteAsync("stderr", $"before {secret} {encoded} \u96ea", "Error", Token);
        var raw = Encoding.UTF8.GetString(ReadActive(spool));
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain(encoded, raw);
        await spool.CompleteAsync();
        var index = await spool.SnapshotAsync(Token);
        var bytes = ReadAll(spool, index);
        var frame = JsonSerializer.Deserialize(bytes, LogJsonContext.Default.LogFrame)!;
        Assert.Equal("before *** *** \u96ea", frame.Text);
        Assert.Equal("stderr", frame.Stream);
        Assert.Equal("Error", frame.Level);
        Assert.Equal(clock.GetUtcNow(), frame.At);
        Assert.Equal(1, index.Lines);
        Assert.Equal(bytes.Length, index.Bytes);
    }

    [Fact]
    public async Task Concurrent_writers_are_serialized_without_loss_duplication_or_torn_frames()
    {
        using var files = new LogFiles();
        await using var spool = New(files);
        await Task.WhenAll(Enumerable.Range(0, 80).Select(number =>
            Task.Run(() => spool.WriteAsync(number % 2 == 0 ? "stdout" : "stderr", $"frame-{number}", cancellationToken: Token), Token)));
        await spool.CompleteAsync();
        var index = await spool.SnapshotAsync(Token);
        Assert.Equal(80, index.Lines);
        var frames = Frames(ReadAll(spool, index));
        Assert.Equal(80, frames.Length);
        Assert.Equal(80, frames.Select(frame => frame.Text).Distinct().Count());
        Assert.All(frames, frame => Assert.StartsWith("frame-", frame.Text));
    }

    [Fact]
    public async Task Cap_retains_record_aligned_head_and_rolling_tail_with_exact_dropped_record_marker()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        await using var spool = New(files, clock: clock, options: new(MaxBytes: 1000, HeadBytes: 300, TailBytes: 300));
        using var original = new MemoryStream();
        for (var number = 0; number < 30; number++)
        {
            var text = $"record-{number:D2}-" + new string('x', 100);
            original.Write(Frame(clock, text));
            await spool.WriteAsync("stdout", text, cancellationToken: Token);
        }
        await spool.CompleteAsync();
        var index = await spool.SnapshotAsync(Token);
        var bytes = original.ToArray();
        Assert.True(index.Truncated);
        Assert.True(index.Complete);
        Assert.Equal(bytes.Length, index.Bytes);
        Assert.Equal(30, index.Lines);
        Assert.InRange(index.Chunks.Sum(chunk => chunk.KeepEnd - chunk.KeepStart), 1, 600);
        var retained = Frames(ReadRetained(spool, index));
        Assert.Equal(new[] { "record-00-", "record-29-" }, retained.Select(frame => frame.Text[..10]));
        Assert.Equal(28, index.DroppedLines);
        Assert.Contains(index.DroppedLines.ToString(), index.TruncationMarker!);
        Assert.Equal(bytes[..(int)index.RetainedHeadEnd!].Concat(bytes[(int)index.RetainedTailStart!..]).ToArray(),
            ReadRetained(spool, index));
        Assert.Equal(2, index.Chunks.Count);
        Assert.Equal(index.Chunks[0].Name, index.Chunks[1].Name);
    }

    [Fact]
    public async Task Capped_rotation_removes_middle_files_and_never_reuses_chunk_numbers()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        await using var spool = New(files, clock: clock, options: new(MaxBytes: 2000, HeadBytes: 400, TailBytes: 400));
        using var expected = new MemoryStream();
        for (var number = 0; number < 16; number++)
        {
            var text = $"record-{number:D2}-" + new string('x', 100);
            expected.Write(Frame(clock, text));
            await spool.WriteAsync("stdout", text, cancellationToken: Token);
            clock.Advance(LogSpooler.ChunkInterval);
            await UntilAsync(async () => (await spool.SnapshotAsync(Token)).NextChunk == number + 1);
        }
        await spool.CompleteAsync();
        var index = await spool.SnapshotAsync(Token);
        var bytes = expected.ToArray();
        Assert.Equal(16, index.NextChunk);
        Assert.InRange(ReadRetained(spool, index).Length, 1, 800);
        Assert.Equal(bytes[..(int)index.RetainedHeadEnd!].Concat(bytes[(int)index.RetainedTailStart!..]).ToArray(), ReadRetained(spool, index));
        var windowRecords = 400 / Frame(clock, "record-00-" + new string('x', 100)).Length;
        var expectedNames = Enumerable.Range(0, windowRecords).Concat(Enumerable.Range(16 - windowRecords, windowRecords))
            .Select(number => $"{number:D6}.log.gz").ToArray();
        Assert.Equal(expectedNames,
            Directory.GetFiles(spool.DirectoryPath, "*.log.gz").Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(index.Lines - Frames(ReadRetained(spool, index)).Length, index.DroppedLines);
    }

    [Fact]
    public async Task Completion_flushes_once_and_rejects_late_producers()
    {
        using var files = new LogFiles();
        await using var spool = New(files);
        await spool.WriteAsync("stdout", "last bytes", cancellationToken: Token);
        await spool.CompleteAsync();
        var before = File.ReadAllBytes(Path.Combine(spool.DirectoryPath, "index.json"));
        await spool.CompleteAsync();
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(spool.DirectoryPath, "index.json")));
        Assert.Equal(0, new FileInfo(Path.Combine(spool.DirectoryPath, "active.raw")).Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => spool.WriteAsync("stdout", "late", cancellationToken: Token));
    }

    [Fact]
    public async Task Restart_recovers_sealed_and_active_data_and_discards_only_unacknowledged_suffix()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        var attempt = Attempt();
        string directory;
        long expectedBytes;
        await using (var original = New(files, attempt, clock))
        {
            directory = original.DirectoryPath;
            await original.WriteAsync("stdout", "first", cancellationToken: Token);
            clock.Advance(LogSpooler.ChunkInterval);
            await UntilAsync(async () => (await original.SnapshotAsync(Token)).Chunks.Count == 1);
            await original.WriteAsync("stderr", "second", cancellationToken: Token);
            expectedBytes = (await original.SnapshotAsync(Token)).Bytes;
        }
        File.AppendAllText(Path.Combine(directory, "active.raw"), "unacknowledged suffix");
        await using var recovered = New(files, attempt, clock);
        Assert.Equal(expectedBytes, (await recovered.SnapshotAsync(Token)).Bytes);
        await recovered.CompleteAsync();
        var index = await recovered.SnapshotAsync(Token);
        Assert.Equal(new[] { "first", "second" }, Frames(ReadAll(recovered, index)).Select(frame => frame.Text));
        Assert.Equal(2, index.Lines);
        Assert.Equal(expectedBytes, index.Bytes);
    }

    [Fact]
    public async Task Restart_after_seal_metadata_commit_before_raw_truncation_does_not_duplicate_bytes()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        var attempt = Attempt();
        string directory;
        byte[] raw;
        await using (var original = New(files, attempt, clock))
        {
            directory = original.DirectoryPath;
            await original.WriteAsync("stdout", "durable", cancellationToken: Token);
            raw = ReadActive(original);
            clock.Advance(LogSpooler.ChunkInterval);
            await UntilAsync(async () => (await original.SnapshotAsync(Token)).Chunks.Count == 1);
        }
        File.WriteAllBytes(Path.Combine(directory, "active.raw"), raw);
        await using var recovered = New(files, attempt, clock);
        Assert.Equal(0, new FileInfo(Path.Combine(directory, "active.raw")).Length);
        await recovered.WriteAsync("stdout", "after restart", cancellationToken: Token);
        await recovered.CompleteAsync();
        var index = await recovered.SnapshotAsync(Token);
        Assert.Equal(new[] { "durable", "after restart" }, Frames(ReadAll(recovered, index)).Select(frame => frame.Text));
    }

    [Fact]
    public async Task Restart_keeps_original_rotation_deadline_and_original_cap_policy()
    {
        using var files = new LogFiles();
        var clock = new FootprintClock();
        var attempt = Attempt();
        var options = new LogSpoolOptions(MaxBytes: 2000, HeadBytes: 300, TailBytes: 300);
        await using (var original = New(files, attempt, clock, options: options))
        {
            await original.WriteAsync("stdout", "first", cancellationToken: Token);
            clock.Advance(TimeSpan.FromSeconds(7));
        }
        await using var recovered = New(files, attempt, clock);
        var state = await recovered.SnapshotAsync(Token);
        Assert.Equal(2000, state.MaxBytes);
        Assert.Equal(300, state.HeadBytes);
        clock.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.Empty((await recovered.SnapshotAsync(Token)).Chunks);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await UntilAsync(async () => (await recovered.SnapshotAsync(Token)).Chunks.Count == 1);
    }

    [Fact]
    public async Task Capped_index_survives_restart_without_losing_slices_or_duplicating_record_counts()
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        byte[] retained;
        long dropped;
        await using (var original = New(files, attempt, options: new(MaxBytes: 1000, HeadBytes: 300, TailBytes: 300)))
        {
            for (var number = 0; number < 20; number++)
                await original.WriteAsync("stdout", $"record-{number:D2}-" + new string('x', 100), cancellationToken: Token);
            await original.CompleteAsync();
            var index = await original.SnapshotAsync(Token);
            retained = ReadRetained(original, index);
            dropped = index.DroppedLines;
        }
        await using var recovered = New(files, attempt);
        var restored = await recovered.SnapshotAsync(Token);
        Assert.True(restored.Truncated);
        Assert.Equal(dropped, restored.DroppedLines);
        Assert.Equal(retained, ReadRetained(recovered, restored));
        Assert.Equal(2, Frames(retained).Length);
    }

    [Fact]
    public async Task Timer_disk_failure_is_observable_and_disposal_still_releases_the_attempt_lease()
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        var clock = new FootprintClock();
        var spool = New(files, attempt, clock);
        await spool.WriteAsync("stdout", "pending", cancellationToken: Token);
        var obstruction = Path.Combine(spool.DirectoryPath, "index.json.pending");
        Directory.CreateDirectory(obstruction);
        clock.Advance(LogSpooler.ChunkInterval);
        var error = await Record.ExceptionAsync(() => spool.TimerCompletion.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(error is IOException or UnauthorizedAccessException);
        await Assert.ThrowsAsync<IOException>(() => spool.SnapshotAsync(Token));
        var disposeError = await Record.ExceptionAsync(() => spool.DisposeAsync().AsTask());
        Assert.True(disposeError is IOException or UnauthorizedAccessException);
        Directory.Delete(obstruction);
        await using var restored = New(files, attempt, clock);
        await restored.CompleteAsync();
        Assert.Equal("pending", Assert.Single(Frames(ReadAll(restored, await restored.SnapshotAsync(Token)))).Text);
    }

    [Fact]
    public async Task Spool_directory_permissions_are_owner_only_on_the_executing_OS()
    {
        using var files = new LogFiles();
        await using var spool = New(files);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var rules = new DirectoryInfo(spool.DirectoryPath).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            var rule = Assert.IsType<FileSystemAccessRule>(Assert.Single(rules.Cast<AuthorizationRule>()));
            Assert.Equal(identity.User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.False(rule.IsInherited);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(spool.DirectoryPath));
    }

    [Fact]
    public async Task Corrupt_chunks_and_identity_mismatch_fail_closed_instead_of_resetting()
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        string directory;
        await using (var original = New(files, attempt))
        {
            directory = original.DirectoryPath;
            await original.WriteAsync("stdout", "keep", cancellationToken: Token);
            await original.CompleteAsync();
        }
        var path = Path.Combine(directory, "000000.log.gz");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => New(files, attempt));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Oversized_recovered_index_is_rejected_before_deserialization_or_unbounded_allocation()
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        string path;
        await using (var spool = New(files, attempt)) path = Path.Combine(spool.DirectoryPath, "index.json");
        using (var output = File.OpenWrite(path)) output.SetLength(LogSpooler.MaximumIndexBytes + 1L);
        Assert.Throws<InvalidDataException>(() => New(files, attempt));
        Assert.Equal(LogSpooler.MaximumIndexBytes + 1L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Disk_failure_is_explicit_and_cannot_publish_or_continue_on_uncommitted_state()
    {
        using var files = new LogFiles();
        var attempt = Attempt();
        string directory;
        await using (var spool = New(files, attempt))
        {
            directory = spool.DirectoryPath;
            Directory.CreateDirectory(Path.Combine(directory, "index.json.pending"));
            await Assert.ThrowsAnyAsync<Exception>(() => spool.WriteAsync("stdout", "not acknowledged", cancellationToken: Token));
            await Assert.ThrowsAsync<IOException>(() => spool.WriteAsync("stdout", "must not continue", cancellationToken: Token));
        }
        Directory.Delete(Path.Combine(directory, "index.json.pending"));
        await using var recovered = New(files, attempt);
        Assert.Equal(0, (await recovered.SnapshotAsync(Token)).Bytes);
        Assert.Equal(0, new FileInfo(Path.Combine(directory, "active.raw")).Length);
    }

    [Theory]
    [InlineData("network", "Information", "text")]
    [InlineData("stdout", "credential", "text")]
    public async Task Invalid_stream_or_level_is_rejected_before_persistence(string stream, string level, string text)
    {
        using var files = new LogFiles();
        await using var spool = New(files);
        await Assert.ThrowsAsync<ArgumentException>(() => spool.WriteAsync(stream, text, level, Token));
        Assert.Equal(0, (await spool.SnapshotAsync(Token)).Bytes);
        await Assert.ThrowsAsync<ArgumentException>(() => spool.WriteAsync("stdout", new string('x', 65537), cancellationToken: Token));
    }

    private static LogSpooler New(LogFiles files, AttemptRef? attempt = null, FootprintClock? clock = null,
        SecretMasker? masker = null, LogSpoolOptions? options = null) =>
        new(files.Root, attempt ?? Attempt(), masker ?? new([]), options, clock);

    internal static AttemptRef Attempt() => new() { RunId = Guid.NewGuid().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 };
    internal static byte[] Frame(TimeProvider clock, string text, string stream = "stdout") =>
        [.. JsonSerializer.SerializeToUtf8Bytes(new LogFrame(clock.GetUtcNow(), stream, "Information", text), LogJsonContext.Default.LogFrame), (byte)'\n'];

    private static async Task FillAsync(LogSpooler spool, TimeProvider clock, long bytes, Stream? expected = null)
    {
        var overhead = Frame(clock, "").Length;
        while (bytes > 0)
        {
            var count = (int)Math.Min(64 * 1024, bytes - overhead);
            Assert.True(count >= 0);
            var text = new string('x', count);
            var frame = Frame(clock, text);
            expected?.Write(frame);
            await spool.WriteAsync("stdout", text, cancellationToken: Token);
            bytes -= frame.Length;
        }
    }

    internal static byte[] ReadAll(LogSpooler spool, LogIndex index) => index.Chunks.DistinctBy(chunk => chunk.Number)
        .SelectMany(chunk => Decompress(File.ReadAllBytes(Path.Combine(spool.DirectoryPath, chunk.Name)))).ToArray();

    internal static byte[] ReadRetained(LogSpooler spool, LogIndex index) => index.Chunks.SelectMany(chunk =>
    {
        var bytes = Decompress(File.ReadAllBytes(Path.Combine(spool.DirectoryPath, chunk.Name)));
        return bytes.AsSpan((int)(chunk.KeepStart - chunk.ByteStart), (int)(chunk.KeepEnd - chunk.KeepStart)).ToArray();
    }).ToArray();

    internal static byte[] Decompress(byte[] bytes)
    {
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var result = new MemoryStream();
        gzip.CopyTo(result);
        return result.ToArray();
    }

    private static byte[] ReadActive(LogSpooler spool)
    {
        using var input = new FileStream(Path.Combine(spool.DirectoryPath, "active.raw"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    internal static LogFrame[] Frames(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize(line, LogJsonContext.Default.LogFrame)!).ToArray();

    internal static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(5, timeout.Token);
    }
}

internal sealed class LogFiles : IDisposable
{
    public string Root { get; } = Path.GetFullPath(Path.Combine(".log-spool-tests", Guid.NewGuid().ToString("N")));
    public LogFiles() => Directory.CreateDirectory(Root);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
