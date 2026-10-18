using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Logs;

public sealed record LogChunk(int Number, string Name, string Sha256, long CompressedBytes,
    long ByteStart, long ByteEnd, long FirstLine, long LastLine, DateTimeOffset FirstAt, DateTimeOffset LastAt,
    long KeepStart, long KeepEnd, bool Uploaded = false, long RetainedLines = -1, bool StartsAtRecordBoundary = true);

/// <summary>Byte ranges are half-open UTF-8 offsets in the original NDJSON stream; line numbers are one-based.
/// Chunks can split records; concatenate contiguous ranges. Retention cuts only at record boundaries.</summary>
public sealed record LogIndex(int Version, string RunId, string NodeKey, string TaskKey, uint Attempt,
    string Prefix, long Bytes, long Lines, bool Truncated, long DroppedLines, bool Complete,
    List<LogChunk> Chunks, int NextChunk = 0, long SealedBytes = 0, long SealedLines = 0,
    bool CaptureInterrupted = false, DateTimeOffset? ActiveAt = null, DateTimeOffset? LastAt = null,
    long MaxBytes = 2_000_000_000, long HeadBytes = 256_000_000, long TailBytes = 256_000_000,
    long SpoolQuotaBytes = 20_000_000_000, long? RetainedHeadEnd = null, long? RetainedTailStart = null,
    bool SealedAtRecordBoundary = true, ulong HostRecordSequence = 0)
{
    public string? TruncationMarker => Truncated
        ? $"[... {DroppedLines.ToString(CultureInfo.InvariantCulture)} log records dropped ...]" : null;

    public static string PrefixFor(AttemptRef task)
    {
        if (!Guid.TryParse(task.RunId, out var run) || run == Guid.Empty ||
            string.IsNullOrWhiteSpace(task.NodeKey) || string.IsNullOrWhiteSpace(task.TaskKey) ||
            Encoding.UTF8.GetByteCount(task.NodeKey) > 512 || Encoding.UTF8.GetByteCount(task.TaskKey) > 512 ||
            task.NodeKey.Contains('/') || task.TaskKey.Contains('/') || task.Attempt is 0 or > 32767)
            throw new ArgumentException("Logs require a valid run, node, task and attempt.");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            task.NodeKey.Length.ToString(CultureInfo.InvariantCulture) + ":" + task.NodeKey + task.TaskKey)));
        return $"logs/{run:D}/{key}/{task.Attempt.ToString(CultureInfo.InvariantCulture)}/";
    }
}

public sealed record LogUploadRequest(string Name, string Sha256, long Size);
public sealed record LogFrame(DateTimeOffset At, string Stream, string Level, string Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(LogIndex))]
[JsonSerializable(typeof(LogFrame))]
[JsonSerializable(typeof(LogUploadRequest))]
public partial class LogJsonContext : JsonSerializerContext;
