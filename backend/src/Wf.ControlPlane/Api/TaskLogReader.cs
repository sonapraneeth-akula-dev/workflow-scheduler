using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Api;

public sealed record TaskLogBytes(long Line, LogFrame Frame, byte[] Bytes, bool Marker = false);

/// <summary>Memory is bounded by one 8 MiB chunk, its compressed representation, and one 512 KiB record.</summary>
public sealed class TaskLogReader(IBlobStore store)
{
    public const int MaximumIndexBytes = 16 * 1024 * 1024;
    public const int MaximumChunkBytes = 8 * 1024 * 1024;
    public const int MaximumRecordBytes = 512 * 1024;

    public async Task<LogIndex> ReadIndexAsync(AttemptRef attempt, CancellationToken token)
    {
        var prefix = LogIndex.PrefixFor(attempt);
        await using var input = await store.OpenLogReadAsync(prefix + "index.json", MaximumIndexBytes, token);
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + read > MaximumIndexBytes) throw new InvalidDataException("Log index exceeds its limit.");
            bytes.Write(buffer, 0, read);
        }
        var index = JsonSerializer.Deserialize(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), LogJsonContext.Default.LogIndex)
            ?? throw new InvalidDataException("Log index is empty.");
        Validate(index, attempt);
        return index;
    }

    public static void Validate(LogIndex index, AttemptRef attempt)
    {
        if (index.Version != 1 || index.RunId != attempt.RunId || index.NodeKey != attempt.NodeKey ||
            index.TaskKey != attempt.TaskKey || index.Attempt != attempt.Attempt ||
            index.Prefix != LogIndex.PrefixFor(attempt) || index.Bytes < 0 || index.Lines is < 0 or long.MaxValue ||
            index.DroppedLines < 0 || index.DroppedLines > index.Lines || index.Chunks is null ||
            index.Chunks.Count > 50000 || index.NextChunk < 0 ||
            !index.Truncated && index.DroppedLines != 0)
            throw new InvalidDataException("Log index identity or bounds are invalid.");
        long end = 0, retained = 0;
        var head = index.Truncated ? index.RetainedHeadEnd ?? index.Bytes : index.Bytes;
        var tail = index.Truncated ? index.RetainedTailStart ?? index.Bytes : index.Bytes;
        if (head < 0 || head > tail || tail > index.Bytes) throw new InvalidDataException("Invalid retained windows.");
        var identities = new Dictionary<int, LogChunk>();
        LogChunk? previous = null;
        foreach (var chunk in index.Chunks)
        {
            if (chunk is null || chunk.Number < 0 || chunk.Number >= index.NextChunk ||
                chunk.Name != $"{chunk.Number:D6}.log.gz" || chunk.Sha256 is null ||
                chunk.Sha256.Length != 64 || !chunk.Sha256.All(char.IsAsciiHexDigit) ||
                chunk.CompressedBytes is <= 0 or > MaximumChunkBytes + 65536 ||
                chunk.ByteStart < 0 || chunk.ByteEnd <= chunk.ByteStart || chunk.ByteEnd > index.Bytes ||
                chunk.ByteEnd - chunk.ByteStart > MaximumChunkBytes ||
                chunk.KeepStart < chunk.ByteStart || chunk.KeepEnd > chunk.ByteEnd || chunk.KeepStart >= chunk.KeepEnd ||
                chunk.FirstLine < 1 || chunk.LastLine < chunk.FirstLine || chunk.LastLine > index.Lines + (index.Complete ? 0 : 1) ||
                chunk.RetainedLines < 0 || chunk.RetainedLines > chunk.LastLine - chunk.FirstLine + 1 ||
                !chunk.Uploaded || chunk.KeepStart < end ||
                !(chunk.KeepEnd <= head || chunk.KeepStart >= tail))
                throw new InvalidDataException("Invalid retained log chunk.");
            if (chunk.ByteStart == 0 && (chunk.FirstLine != 1 || !chunk.StartsAtRecordBoundary) ||
                index.Complete && chunk.ByteEnd == index.Bytes && chunk.LastLine != index.Lines ||
                previous is not null && previous.Number != chunk.Number &&
                (chunk.Number <= previous.Number || chunk.ByteStart < previous.ByteEnd ||
                    chunk.ByteStart == previous.ByteEnd &&
                    chunk.FirstLine != previous.LastLine + (chunk.StartsAtRecordBoundary ? 1 : 0)))
                throw new InvalidDataException("Log chunk ordering or line numbers are inconsistent.");
            if (identities.TryGetValue(chunk.Number, out var prior) &&
                prior with { KeepStart = chunk.KeepStart, KeepEnd = chunk.KeepEnd, RetainedLines = chunk.RetainedLines } != chunk)
                throw new InvalidDataException("Conflicting log chunk identities.");
            identities[chunk.Number] = chunk;
            if (end == head) end = tail;
            if (chunk.KeepStart != end) throw new InvalidDataException("Missing retained log bytes.");
            end = chunk.KeepEnd;
            retained = checked(retained + chunk.RetainedLines);
            previous = chunk;
        }
        if (end == head) end = tail;
        if (end != index.Bytes || retained != index.Lines - index.DroppedLines ||
            index.Complete && (!index.SealedAtRecordBoundary || index.SealedBytes != index.Bytes || index.SealedLines != index.Lines))
            throw new InvalidDataException("Incomplete retained log index.");
    }

    public async IAsyncEnumerable<TaskLogBytes> ReadAsync(LogIndex index, long firstLine = 1, long lastLine = long.MaxValue,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        using var record = new MemoryStream();
        long previousEnd = -1, line = 1;
        var markerWritten = false;
        byte[] raw = [];
        var loaded = -1;
        foreach (var slice in index.Chunks)
        {
            token.ThrowIfCancellationRequested();
            if (slice.LastLine < firstLine || slice.FirstLine > lastLine) continue;
            if (loaded != slice.Number)
            {
                raw = await ReadChunkAsync(index.Prefix, slice, token);
                loaded = slice.Number;
            }
            var start = checked((int)(slice.KeepStart - slice.ByteStart));
            var end = checked((int)(slice.KeepEnd - slice.ByteStart));
            if (slice.RetainedLines != raw.AsSpan(start, end - start).Count((byte)'\n'))
                throw new InvalidDataException("Retained record count is inconsistent.");
            if (previousEnd != slice.KeepStart)
            {
                if (record.Length != 0) throw new InvalidDataException("A retained record is incomplete.");
                line = slice.FirstLine + raw.AsSpan(0, start).Count((byte)'\n');
                if (start == 0 ? !slice.StartsAtRecordBoundary : raw[start - 1] != '\n')
                {
                    // An indexed seek may start in the continuation of the previous, out-of-range record.
                    if (slice.KeepStart != slice.ByteStart || line >= firstLine)
                        throw new InvalidDataException("A retained window does not begin at a record boundary.");
                    var newline = raw.AsSpan(start, end - start).IndexOf((byte)'\n');
                    if (newline < 0) { previousEnd = slice.KeepEnd; continue; }
                    start += newline + 1;
                    line++;
                }
                if (index.Truncated && !markerWritten && slice.KeepStart >= index.RetainedTailStart &&
                    index.DroppedLines > 0)
                {
                    markerWritten = true;
                    var marker = new LogFrame(slice.FirstAt, "system", "Warning", index.TruncationMarker!);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, LogJsonContext.Default.LogFrame);
                    yield return new(line, marker, [.. bytes, (byte)'\n'], true);
                }
            }
            while (start < end)
            {
                token.ThrowIfCancellationRequested();
                var newline = raw.AsSpan(start, end - start).IndexOf((byte)'\n');
                var count = newline < 0 ? end - start : newline + 1;
                if (record.Length + count > MaximumRecordBytes) throw new InvalidDataException("Log record exceeds its limit.");
                record.Write(raw, start, count);
                start += count;
                if (newline < 0) break;
                var bytes = record.ToArray();
                record.SetLength(0);
                if (line >= firstLine && line <= lastLine)
                {
                    var frame = JsonSerializer.Deserialize(bytes.AsSpan(0, bytes.Length - 1), LogJsonContext.Default.LogFrame)
                        ?? throw new InvalidDataException("Empty log frame.");
                    if (frame.Text is null || frame.Text.Length > 65536 ||
                        frame.Stream is not ("stdout" or "stderr" or "ctx") ||
                        frame.Level is not ("Information" or "Warning" or "Error" or "Debug"))
                        throw new InvalidDataException("Invalid log frame.");
                    yield return new(line, frame, bytes);
                }
                line++;
                if (line > lastLine) yield break;
            }
            previousEnd = slice.KeepEnd;
        }
        if (record.Length != 0) throw new InvalidDataException("The final retained log record is incomplete.");
        if (index.Truncated && !markerWritten && index.DroppedLines > 0)
        {
            var marker = new LogFrame(index.LastAt ?? DateTimeOffset.UnixEpoch, "system", "Warning", index.TruncationMarker!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, LogJsonContext.Default.LogFrame);
            yield return new(index.Lines + 1, marker, [.. bytes, (byte)'\n'], true);
        }
    }

    private async Task<byte[]> ReadChunkAsync(string prefix, LogChunk chunk, CancellationToken token)
    {
        using var compressed = new MemoryStream(checked((int)chunk.CompressedBytes));
        await store.DownloadAsync(new(BlobKind.Logs, chunk.Sha256, chunk.CompressedBytes, prefix + chunk.Name), compressed, token);
        compressed.Position = 0;
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        var raw = new byte[checked((int)(chunk.ByteEnd - chunk.ByteStart))];
        await gzip.ReadExactlyAsync(raw, token);
        if (await gzip.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Log chunk expands past its index.");
        var last = chunk.FirstLine + raw.AsSpan().Count((byte)'\n') - (raw[^1] == '\n' ? 1 : 0);
        if (last != chunk.LastLine) throw new InvalidDataException("Log chunk line numbers do not match its contents.");
        return raw;
    }
}
