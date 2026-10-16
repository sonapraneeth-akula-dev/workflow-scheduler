using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Wf.Agent.Hosts;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;

namespace Wf.Agent.Logs;

public sealed record LogSpoolOptions(long MaxBytes = 2_000_000_000, long HeadBytes = 256_000_000,
    long TailBytes = 256_000_000, long SpoolQuotaBytes = 20_000_000_000)
{
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HeadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TailBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SpoolQuotaBytes);
    }
}

/// <summary>One writer lease per attempt. Only masked frames reach disk. State replacement follows fsync;
/// upload acknowledgement is durable before any spool bytes are removed.</summary>
public sealed class LogSpooler : IAsyncDisposable
{
    public const int ChunkBytes = 8 * 1024 * 1024;
    public const int MaximumIndexBytes = 16 * 1024 * 1024;
    public static readonly TimeSpan ChunkInterval = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim gate = new(1);
    private readonly FileStream lease;
    private readonly TimeProvider clock;
    private readonly LogSpoolOptions options;
    private readonly SecretMasker masker;
    private readonly string root;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte> rotations = Channel.CreateBounded<byte>(1);
    private readonly ITimer rotationTimer;
    private readonly Task timer;
    private FileStream? current;
    private long activeStart;
    private long activeFirstLine;
    private DateTimeOffset activeAt;
    private DateTimeOffset lastAt;
    private long opened;
    private bool disposed;
    private Exception? backgroundError;
    private LogIndex index;

    public string DirectoryPath { get; }
    public Task TimerCompletion => timer;
    internal bool IsComplete => Volatile.Read(ref index).Complete;

    public LogSpooler(string root, AttemptRef attempt, SecretMasker masker, LogSpoolOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var prefix = LogIndex.PrefixFor(attempt);
        this.options = options ?? new();
        this.options.Validate();
        this.masker = masker ?? throw new ArgumentNullException(nameof(masker));
        this.root = Path.GetFullPath(root);
        clock = timeProvider ?? TimeProvider.System;
        DirectoryPath = Path.Combine(Path.GetFullPath(root), prefix.Replace('/', Path.DirectorySeparatorChar));
        RejectLinks(DirectoryPath);
        TaskHostPool.CreatePrivateDirectory(DirectoryPath);
        RejectLinks(DirectoryPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(DirectoryPath)) RejectLinks(entry);
        lease = new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(DirectoryPath, "index.json");
            index = File.Exists(path) ? ReadIndex(path) :
                new(1, Guid.Parse(attempt.RunId).ToString("D"), attempt.NodeKey, attempt.TaskKey, attempt.Attempt,
                    prefix, 0, 0, false, 0, false, [], MaxBytes: this.options.MaxBytes,
                    HeadBytes: this.options.HeadBytes, TailBytes: this.options.TailBytes, SpoolQuotaBytes: this.options.SpoolQuotaBytes);
            this.options = new(index.MaxBytes, index.HeadBytes, index.TailBytes, index.SpoolQuotaBytes);
            this.options.Validate();
            ValidateRecovered(attempt, prefix);
            // Bytes beyond the last committed state were never acknowledged to the producer.
            var committed = index.SealedBytes;
            var activePath = Path.Combine(DirectoryPath, "active.raw");
            if (!index.Complete)
            {
                current = OpenActive();
                if (current.Length < index.Bytes - committed)
                    throw new InvalidDataException("The active log spool is shorter than its committed index.");
                current.SetLength(index.Bytes - committed);
                current.Position = current.Length;
                activeStart = committed;
                activeFirstLine = index.SealedLines + 1;
                activeAt = index.ActiveAt ?? clock.GetUtcNow();
                lastAt = index.LastAt ?? activeAt;
                var age = clock.GetUtcNow() - activeAt;
                age = age < TimeSpan.Zero ? TimeSpan.Zero : age > ChunkInterval ? ChunkInterval : age;
                opened = clock.GetTimestamp() - (long)(age.TotalSeconds * clock.TimestampFrequency);
            }
            else if (File.Exists(activePath)) File.Delete(activePath);
            Save();
            var due = Timeout.InfiniteTimeSpan;
            if (current?.Length > 0)
            {
                due = ChunkInterval - clock.GetElapsedTime(opened);
                if (due < TimeSpan.Zero) due = TimeSpan.Zero;
            }
            rotationTimer = clock.CreateTimer(_ => rotations.Writer.TryWrite(0), null, due, Timeout.InfiniteTimeSpan);
            timer = RotateOnTimeAsync();
        }
        catch { current?.Dispose(); lease.Dispose(); stop.Dispose(); throw; }
    }

    public async Task WriteAsync(string stream, string text, string level = "Information",
        CancellationToken cancellationToken = default)
    {
        if (stream is not ("stdout" or "stderr" or "ctx") || level is not ("Information" or "Warning" or "Error" or "Debug"))
            throw new ArgumentException("Invalid log stream or level.");
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 64 * 1024) throw new ArgumentException("Log frames cannot exceed 64K characters.");
        // Redact the decoded value before JSON encoding (including base64 variants).
        await gate.WaitAsync(cancellationToken);
        try
        {
            Check();
            if (index.Complete) throw new InvalidOperationException("The log attempt has completed.");
            var at = clock.GetUtcNow();
            var frame = JsonSerializer.SerializeToUtf8Bytes(new LogFrame(at, stream, level, masker.Redact(text)),
                LogJsonContext.Default.LogFrame);
            var bytes = new byte[frame.Length + 1];
            frame.CopyTo(bytes, 0);
            bytes[^1] = (byte)'\n';
            var previousChunk = index.NextChunk;
            var offset = 0;
            while (offset < bytes.Length)
            {
                if (current!.Length > 0 && clock.GetElapsedTime(opened) >= ChunkInterval) Seal();
                var count = (int)Math.Min(bytes.Length - offset, ChunkBytes - current!.Length);
                if (current.Length == 0)
                {
                    activeAt = lastAt = at; activeFirstLine = index.Lines + 1; opened = clock.GetTimestamp();
                    rotationTimer.Change(ChunkInterval, Timeout.InfiniteTimeSpan);
                }
                current.Write(bytes, offset, count);
                current.Flush(flushToDisk: true);
                if (at < activeAt) activeAt = at;
                if (at > lastAt) lastAt = at;
                index = index with
                {
                    Bytes = checked(index.Bytes + count),
                    Lines = checked(index.Lines + bytes.AsSpan(offset, count).Count((byte)'\n')),
                    ActiveAt = activeAt, LastAt = lastAt
                };
                offset += count;
                if (current.Length == ChunkBytes) Seal();
                else Save();
            }
            if (index.NextChunk != previousChunk || !index.Truncated &&
                index.Bytes > Math.Min(options.MaxBytes, options.SpoolQuotaBytes))
                ApplyCap(checkGlobalQuota: index.NextChunk != previousChunk);
            Save();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { backgroundError = error; throw; }
        finally { gate.Release(); }
    }

    public async Task<LogIndex> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { Check(); return index with { Chunks = [.. index.Chunks] }; }
        finally { gate.Release(); }
    }

    public async Task CompleteAsync(bool captureInterrupted = false)
    {
        await gate.WaitAsync();
        try
        {
            Check();
            if (index.Complete) return;
            Seal();
            ApplyCap(checkGlobalQuota: true);
            index = index with { Complete = true, CaptureInterrupted = captureInterrupted };
            Save();
            current?.Dispose();
            current = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { backgroundError = error; throw; }
        finally { gate.Release(); }
    }

    internal async Task AcknowledgeAsync(int number)
    {
        await gate.WaitAsync();
        try
        {
            Check();
            var position = index.Chunks.FindIndex(chunk => chunk.Number == number);
            if (position < 0) return; // Retention may have discarded an in-flight chunk.
            for (var item = 0; item < index.Chunks.Count; item++)
                if (index.Chunks[item].Number == number)
                    index.Chunks[item] = index.Chunks[item] with { Uploaded = true };
            Save();
        }
        finally { gate.Release(); }
    }

    internal async Task AcknowledgeIndexAsync()
    {
        await gate.WaitAsync();
        try
        {
            Check();
            if (!index.Complete || index.Chunks.Any(chunk => !chunk.Uploaded))
                throw new InvalidOperationException("Cannot acknowledge an incomplete log index.");
            DurableWrite(Path.Combine(DirectoryPath, "uploaded"), []);
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.log.gz")) File.Delete(path);
        }
        finally { gate.Release(); }
    }

    public bool IsUploaded => File.Exists(Path.Combine(DirectoryPath, "uploaded"));

    private void Seal()
    {
        if (current is null || current.Length == 0) return;
        current.Flush(flushToDisk: true);
        current.Position = 0;
        var number = index.NextChunk;
        var name = $"{number:D6}.log.gz";
        var path = Path.Combine(DirectoryPath, name);
        using (var file = new FileStream(path + ".pending", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true)) current.CopyTo(gzip, 64 * 1024);
            file.Flush(flushToDisk: true);
        }
        File.Move(path + ".pending", path, overwrite: true);
        using var input = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(input));
        current.Position = current.Length - 1;
        var endsAtBoundary = current.ReadByte() == '\n';
        var lastLine = index.Lines + (endsAtBoundary ? 0 : 1);
        index.Chunks.Add(new(number, name, sha, input.Length, activeStart, index.Bytes, activeFirstLine,
            Math.Max(activeFirstLine, lastLine), activeAt, lastAt, activeStart, index.Bytes,
            RetainedLines: index.Lines - index.SealedLines, StartsAtRecordBoundary: index.SealedAtRecordBoundary));
        index = index with
        {
            NextChunk = checked(number + 1), SealedBytes = index.Bytes, SealedLines = index.Lines,
            SealedAtRecordBoundary = endsAtBoundary
        };
        // Commit sealed metadata before truncating active.raw. Recovery ignores that old raw prefix.
        Save();
        current.SetLength(0);
        current.Position = 0;
        current.Flush(flushToDisk: true);
        activeStart = index.Bytes;
        opened = clock.GetTimestamp();
        rotationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void ApplyCap(bool checkGlobalQuota = false)
    {
        var quota = Math.Min(options.MaxBytes, options.SpoolQuotaBytes);
        if (!index.Truncated && index.Bytes <= quota &&
            (!checkGlobalQuota || Files(root, "*")
                .Where(path => path.EndsWith(".log.gz", StringComparison.Ordinal) || Path.GetFileName(path) == "active.raw")
                .Sum(ExistingFileBytes) < options.SpoolQuotaBytes))
            return;
        var headBudget = Math.Min(options.HeadBytes, quota / 2);
        var tail = Math.Min(options.TailBytes, quota - headBudget);
        var overlap = index.Bytes <= headBudget + tail;
        var head = overlap ? index.Bytes : index.RetainedHeadEnd ?? Boundary(headBudget, backwards: true);
        var tailStart = overlap ? index.Bytes : Boundary(Math.Max(headBudget, index.Bytes - tail), backwards: false);
        long retainedLines = 0;
        var kept = new List<LogChunk>();
        var removed = new List<string>();
        foreach (var chunk in index.Chunks.DistinctBy(chunk => chunk.Number))
        {
            var start = chunk.ByteStart < head ? chunk.ByteStart : Math.Max(chunk.ByteStart, tailStart);
            var end = chunk.ByteStart < head ? Math.Min(chunk.ByteEnd, head) : chunk.ByteEnd;
            // A single chunk can contain both windows; keep two logical slices of the same blob.
            if (chunk.ByteStart < head && chunk.ByteEnd > tailStart)
            {
                kept.Add(Slice(chunk, start, end));
                start = tailStart;
                end = chunk.ByteEnd;
            }
            if (start < end)
            {
                kept.Add(Slice(chunk, start, end));
            }
            else removed.Add(Path.Combine(DirectoryPath, chunk.Name));
        }
        foreach (var group in kept.GroupBy(chunk => chunk.Number))
        {
            if (group.All(slice => slice.RetainedLines >= 0))
            {
                retainedLines += group.Sum(slice => slice.RetainedLines);
                continue;
            }
            var path = Path.Combine(DirectoryPath, group.First().Name);
            if (!File.Exists(path)) continue;
            using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var memory = new MemoryStream();
            gzip.CopyTo(memory);
            foreach (var slice in group)
            {
                var lines = memory.GetBuffer().AsSpan(checked((int)(slice.KeepStart - slice.ByteStart)),
                    checked((int)(slice.KeepEnd - slice.KeepStart))).Count((byte)'\n');
                retainedLines += lines;
                kept[kept.IndexOf(slice)] = slice with { RetainedLines = lines };
            }
        }
        if (current is not null && current.Length > 0)
        {
            var raw = ReadActive();
            var headLength = Math.Clamp(head - activeStart, 0, raw.Length);
            var tailOffset = Math.Clamp(tailStart - activeStart, 0, raw.Length);
            retainedLines += raw.AsSpan(0, (int)headLength).Count((byte)'\n');
            retainedLines += raw.AsSpan((int)tailOffset).Count((byte)'\n');
        }
        index = index with
        {
            Truncated = true, DroppedLines = index.Lines - retainedLines, Chunks = kept,
            RetainedHeadEnd = overlap ? null : head, RetainedTailStart = tailStart
        };
        Save();
        foreach (var path in removed) File.Delete(path);

        LogChunk Slice(LogChunk chunk, long start, long end)
        {
            var prior = index.Chunks.FirstOrDefault(item => item.Number == chunk.Number &&
                item.KeepStart == start && item.KeepEnd == end);
            return chunk with { KeepStart = start, KeepEnd = end, RetainedLines = prior?.RetainedLines ?? -1 };
        }

        long Boundary(long desired, bool backwards)
        {
            if (desired <= 0) return 0;
            if (backwards && current?.Length > 0 && activeStart < desired)
            {
                var activeBoundary = Find(ReadActive(), activeStart, index.SealedAtRecordBoundary);
                if (activeBoundary is not null) return activeBoundary.Value;
            }
            var chunks = index.Chunks.DistinctBy(chunk => chunk.Number);
            if (backwards) chunks = chunks.OrderByDescending(chunk => chunk.ByteStart);
            foreach (var chunk in chunks)
            {
                if (backwards ? chunk.ByteStart >= desired : chunk.ByteEnd <= desired) continue;
                using var gzip = new GZipStream(File.OpenRead(Path.Combine(DirectoryPath, chunk.Name)), CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                var boundary = Find(output.GetBuffer().AsSpan(0, (int)output.Length), chunk.ByteStart, chunk.StartsAtRecordBoundary);
                if (boundary is not null) return boundary.Value;
            }
            if (current?.Length > 0)
            {
                var boundary = Find(ReadActive(), activeStart, index.SealedAtRecordBoundary);
                if (boundary is not null) return boundary.Value;
            }
            return backwards ? 0 : index.Bytes;

            long? Find(ReadOnlySpan<byte> raw, long start, bool startsAtBoundary)
            {
                var offset = (int)Math.Clamp(desired - start, 0, raw.Length);
                if (!backwards && (offset == 0 ? startsAtBoundary : raw[offset - 1] == '\n')) return start + offset;
                var newline = backwards ? raw[..offset].LastIndexOf((byte)'\n') :
                    raw[offset..].IndexOf((byte)'\n');
                return newline < 0 ? null : start + (backwards ? newline : offset + newline) + 1;
            }
        }
    }

    private byte[] ReadActive()
    {
        var position = current!.Position;
        current.Position = 0;
        var bytes = new byte[checked((int)current.Length)];
        current.ReadExactly(bytes);
        current.Position = position;
        return bytes;
    }

    private async Task RotateOnTimeAsync()
    {
        try
        {
            await foreach (var _ in rotations.Reader.ReadAllAsync(stop.Token))
            {
                await gate.WaitAsync(stop.Token);
                try
                {
                    if (!index.Complete && current?.Length > 0 && clock.GetElapsedTime(opened) >= ChunkInterval)
                    {
                        Seal();
                        ApplyCap(checkGlobalQuota: true);
                        Save();
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            backgroundError = error;
            throw;
        }
    }

    private void ValidateRecovered(AttemptRef attempt, string prefix)
    {
        if (index.Version != 1 || index.Prefix != prefix || index.RunId != Guid.Parse(attempt.RunId).ToString("D") ||
            index.NodeKey != attempt.NodeKey || index.TaskKey != attempt.TaskKey || index.Attempt != attempt.Attempt ||
            index.Bytes < 0 || index.Lines < 0 || index.SealedBytes < 0 || index.SealedBytes > index.Bytes ||
            index.Bytes - index.SealedBytes > ChunkBytes || index.SealedLines < 0 || index.SealedLines > index.Lines ||
            index.NextChunk < 0 || index.Chunks.Any(chunk => chunk.Number < 0 || chunk.Number >= index.NextChunk ||
                chunk.Name != $"{chunk.Number:D6}.log.gz" || chunk.ByteStart < 0 || chunk.ByteEnd > index.Bytes ||
                chunk.ByteEnd <= chunk.ByteStart || chunk.ByteEnd - chunk.ByteStart > ChunkBytes ||
                chunk.CompressedBytes <= 0 || chunk.Sha256.Length != 64 || !chunk.Sha256.All(char.IsAsciiHexDigit) ||
                chunk.KeepStart < chunk.ByteStart || chunk.KeepEnd > chunk.ByteEnd || chunk.KeepEnd <= chunk.KeepStart))
            throw new InvalidDataException("Log index identity or ranges are invalid.");
        foreach (var chunk in index.Chunks.DistinctBy(chunk => chunk.Number))
        {
            var path = Path.Combine(DirectoryPath, chunk.Name);
            if (!File.Exists(path) && IsUploaded && index.Complete) continue;
            using var input = File.OpenRead(path);
            if (input.Length != chunk.CompressedBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != chunk.Sha256)
                throw new InvalidDataException("Log chunk integrity check failed.");
        }
    }

    private FileStream OpenActive() => new(Path.Combine(DirectoryPath, "active.raw"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private void Save()
    {
        try
        {
            DurableWrite(Path.Combine(DirectoryPath, "index.json"),
                JsonSerializer.SerializeToUtf8Bytes(index, LogJsonContext.Default.LogIndex));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { backgroundError = error; throw; }
    }

    internal static void DurableWrite(string path, byte[] bytes)
    {
        using (var file = new FileStream(path + ".pending", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(path + ".pending", path, overwrite: true);
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (backgroundError is not null) throw new IOException("Log spool rotation failed; inspect the agent disk.", backgroundError);
    }

    internal static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Log spool paths cannot contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static IEnumerable<string> Files(string root, string pattern) => Directory.EnumerateFiles(root, pattern,
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });

    internal static LogIndex ReadIndex(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 or > MaximumIndexBytes)
            throw new InvalidDataException("Log index exceeds its 16 MiB limit or is empty.");
        return JsonSerializer.Deserialize(input, LogJsonContext.Default.LogIndex) ?? throw new InvalidDataException("Empty log index.");
    }

    private static long ExistingFileBytes(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; } // A different attempt may have just acknowledged and deleted it.
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await stop.CancelAsync();
        try { await timer; }
        finally
        {
            await gate.WaitAsync();
            try { disposed = true; rotationTimer.Dispose(); current?.Dispose(); lease.Dispose(); stop.Dispose(); }
            finally { gate.Release(); }
        }
    }
}
