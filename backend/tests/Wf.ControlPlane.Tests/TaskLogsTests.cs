using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Tests;

public sealed class TaskLogsTests
{
    private static AttemptRef Attempt => new() { RunId = Guid.CreateVersion7().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 };

    [Fact]
    public void TaskLogs_defaults_and_canonical_identity()
    {
        Assert.Equal(new(0, 200, null, false, 1, 0), TaskLogsQuery.Parse(QueryCollection.Empty));
        var attempt = Attempt;
        Assert.Equal(attempt, TaskLogsQuery.Identity(attempt.RunId, "node%2Ftask", "1"));
        var query = Parse(("search", "😀"), ("cursor", TaskLogsQuery.Cursor(23, 4)));
        Assert.Equal((23L, 4), (query.AfterLine, query.AfterIndex));
        Assert.Equal("task%2Fsecret", TaskLogsQuery.Identity(attempt.RunId, "node%2Ftask%252Fsecret", "1").TaskKey);
        Assert.Throws<ArgumentException>(() => TaskLogsQuery.Identity(Guid.NewGuid().ToString(), "node%2Ftask", "1"));
        Assert.Throws<ArgumentException>(() => TaskLogsQuery.Identity("0199E770-0000-7000-8000-ABCDEFABCDEF", "node%2Ftask", "1"));
    }

    [Fact]
    public void TaskLogs_source_generated_contract_preserves_unicode_and_does_not_expose_storage_fields()
    {
        var page = new TaskLogsPage(1, 0, false, false, null, [new(1, Frame("😀\r\n***"))], [], null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(page, TaskLogsJsonContext.Default.TaskLogsPage);
        var parsed = JsonSerializer.Deserialize(bytes, TaskLogsJsonContext.Default.TaskLogsPage)!;
        Assert.Equal("😀\r\n***", Assert.Single(parsed.Records).Frame.Text);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("prefix", json);
        Assert.DoesNotContain("sha256", json);
    }

    [Theory]
    [InlineData("offset", "-1")]
    [InlineData("offset", "01")]
    [InlineData("offset", "9223372036854775807")]
    [InlineData("length", "0")]
    [InlineData("length", "1001")]
    [InlineData("length", "+1")]
    [InlineData("length", " 1")]
    [InlineData("search", "")]
    [InlineData("download", "false")]
    [InlineData("cursor", "bad")]
    [InlineData("unsupported", "x")]
    public void TaskLogs_invalid_query_is_explicit(string key, string value) =>
        Assert.Throws<ArgumentException>(() => Parse((key, value)));

    [Fact]
    public void TaskLogs_rejects_duplicate_mixed_and_noncanonical_cursor_queries()
    {
        Assert.Throws<ArgumentException>(() => TaskLogsQuery.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { ["offset"] = new(["1", "2"]) })));
        foreach (var pairs in new[]
        {
            new[] { ("search", "a"), ("length", "1") },
            new[] { ("download", "true"), ("search", "a") },
            new[] { ("search", "a"), ("cursor", "!!!") },
            new[] { ("search", new string('x', 2049)) }
        }) Assert.Throws<ArgumentException>(() => Parse(pairs));
    }

    [Theory]
    [InlineData("node%252Ftask", "1")]
    [InlineData("node%2F..", "1")]
    [InlineData("node%2F..%5Csecret", "1")]
    [InlineData("node%2Ftask%2Fother", "1")]
    [InlineData("node%2Ftask", "01")]
    [InlineData("node%2Ftask", "0")]
    [InlineData("node%2Ftask", "32768")]
    public void TaskLogs_rejects_ambiguous_or_traversal_identity(string task, string attempt) =>
        Assert.Throws<ArgumentException>(() => TaskLogsQuery.Identity(Attempt.RunId, task, attempt));

    [Fact]
    public async Task TaskLogs_retained_slices_of_same_chunk_exclude_original_middle_bytes()
    {
        var attempt = Attempt;
        var frames = new[] { Frame("head😀\r\n***"), Frame("EXCLUDED"), Frame("tail") };
        var store = new LogStore();
        var raw = frames.SelectMany(Bytes).ToArray();
        var chunk = store.Chunk(raw, 0, 0, 1, 3);
        var head = Bytes(frames[0]).Length;
        var tail = raw.Length - Bytes(frames[2]).Length;
        var index = Index(attempt, raw.Length, 3,
            [chunk with { KeepEnd = head, RetainedLines = 1 }, chunk with { KeepStart = tail, RetainedLines = 1 }])
            with { Truncated = true, DroppedLines = 1, RetainedHeadEnd = head, RetainedTailStart = tail };
        TaskLogReader.Validate(index, attempt);
        var output = await Read(new(store), index);
        Assert.Equal(["head😀\r\n***", "[... 1 log records dropped ...]", "tail"], output.Select(item => item.Frame.Text));
        Assert.Equal([1L, 3L], output.Where(item => !item.Marker).Select(item => item.Line));
        Assert.DoesNotContain("EXCLUDED", Encoding.UTF8.GetString(output.SelectMany(item => item.Bytes).ToArray()));
        Assert.Equal(1, store.Downloads);
    }

    [Fact]
    public async Task TaskLogs_indexed_seek_reads_only_relevant_chunk_and_reassembles_split_unicode_record()
    {
        var attempt = Attempt;
        var store = new LogStore();
        var first = Bytes(Frame("one"));
        var second = Bytes(Frame("😀two\r\n***"));
        var split = second.Length / 2;
        var left = first.Concat(second[..split]).ToArray();
        var chunks = new[]
        {
            store.Chunk(left, 0, 0, 1, 2) with { RetainedLines = 1 },
            store.Chunk(second[split..], 1, left.Length, 2, 2) with { StartsAtRecordBoundary = false },
            store.Chunk(Bytes(Frame("three")), 2, first.Length + second.Length, 3, 3)
        };
        var index = Index(attempt, chunks[^1].ByteEnd, 3, [.. chunks]);
        TaskLogReader.Validate(index, attempt);
        var reader = new TaskLogReader(store);
        var page = await Read(reader, index, 3, 3);
        Assert.Equal("three", Assert.Single(page).Frame.Text);
        Assert.Equal(1, store.Downloads);
        store.Downloads = 0;
        var joined = await Read(reader, index, 2, 2);
        Assert.Equal("😀two\r\n***", Assert.Single(joined).Frame.Text);
        Assert.Equal(2, store.Downloads);
    }

    [Fact]
    public async Task TaskLogs_corrupt_gzip_and_decompression_bomb_fail_before_returning_bytes()
    {
        var attempt = Attempt;
        var store = new LogStore();
        var chunk = store.Chunk(Bytes(Frame("log")), 0, 0, 1, 1);
        var reader = new TaskLogReader(store);
        var index = Index(attempt, chunk.ByteEnd, 1, [chunk]);
        store.Content[chunk.Name] = [1, 2, 3];
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(reader, index));
        store = new LogStore();
        chunk = store.Chunk(new byte[4096], 0, 0, 1, 1) with { ByteEnd = 10, KeepEnd = 10 };
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(new(store), Index(attempt, 10, 1, [chunk])));
    }

    [Fact]
    public void TaskLogs_index_rejects_foreign_prefix_gaps_overlap_unuploaded_and_oversized_chunks()
    {
        var attempt = Attempt;
        var store = new LogStore();
        var chunk = store.Chunk(Bytes(Frame("log")), 0, 0, 1, 1);
        var index = Index(attempt, chunk.ByteEnd, 1, [chunk]);
        foreach (var corrupt in new[]
        {
            index with { Prefix = "logs/foreign/" },
            index with { Chunks = [chunk with { Name = "../secret" }] },
            index with { Chunks = [chunk with { Uploaded = false }] },
            index with { Chunks = [chunk with { KeepStart = 1 }] },
            index with { Chunks = [chunk, chunk] },
            index with { Chunks = [chunk with { CompressedBytes = long.MaxValue }] },
            index with { Chunks = [chunk with { ByteEnd = long.MaxValue }] }
        }) Assert.Throws<InvalidDataException>(() => TaskLogReader.Validate(corrupt, attempt));
    }

    private static TaskLogsQuery Parse(params (string Key, string Value)[] pairs) =>
        TaskLogsQuery.Parse(new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value))));
    private static LogFrame Frame(string text) => new(DateTimeOffset.UnixEpoch, "stdout", "Information", text);
    private static byte[] Bytes(LogFrame frame) => [.. JsonSerializer.SerializeToUtf8Bytes(frame, LogJsonContext.Default.LogFrame), (byte)'\n'];
    private static LogIndex Index(AttemptRef attempt, long bytes, long lines, List<LogChunk> chunks) =>
        new(1, attempt.RunId, attempt.NodeKey, attempt.TaskKey, attempt.Attempt, LogIndex.PrefixFor(attempt),
            bytes, lines, false, 0, true, chunks, chunks.Max(item => item.Number) + 1, bytes, lines);
    private static async Task<List<TaskLogBytes>> Read(TaskLogReader reader, LogIndex index, long start = 1, long end = long.MaxValue)
    {
        var result = new List<TaskLogBytes>();
        await foreach (var item in reader.ReadAsync(index, start, end, TestContext.Current.CancellationToken)) result.Add(item);
        return result;
    }

    private sealed class LogStore : IBlobStore
    {
        public readonly Dictionary<string, byte[]> Content = [];
        public int Downloads;
        public LogChunk Chunk(byte[] bytes, int number, long start, long first, long last)
        {
            using var memory = new MemoryStream();
            using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) gzip.Write(bytes);
            var compressed = memory.ToArray();
            var name = $"{number:D6}.log.gz";
            Content[name] = compressed;
            return new(number, name, Convert.ToHexStringLower(SHA256.HashData(compressed)), compressed.Length,
                start, start + bytes.Length, first, last, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                start, start + bytes.Length, true, bytes.Count(value => value == '\n'));
        }
        public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
        {
            Downloads++;
            await destination.WriteAsync(Content[content.Key.Split('/')[^1]], cancellationToken);
        }
    }
}
