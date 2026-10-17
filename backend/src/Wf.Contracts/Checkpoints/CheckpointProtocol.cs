using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Checkpoints;

public sealed record CheckpointReference(Guid RunId, string TaskKey, short Attempt, long Sequence,
    DateTimeOffset SavedAt, string BlobKey, string Sha256, long SizeBytes, ulong ReservationEpoch);

public static class CheckpointProtocol
{
    public const int MaximumBytes = 1024 * 1024;
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);

    public static CheckpointReference Validate(CheckpointSaved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var task = saved.Task;
        if (task is null || !Guid.TryParseExact(task.RunId, "D", out var run) || run.Version != 7 ||
            task.RunId != run.ToString("D") || !ValidKey(task.NodeKey) || !ValidKey(task.TaskKey) ||
            task.Attempt is 0 or > 32767 || saved.ReservationEpoch is 0 or > long.MaxValue ||
            saved.CheckpointSequence is 0 or > long.MaxValue || saved.SizeBytes is 0 or > MaximumBytes ||
            saved.Sha256 is not { Length: 64 } || !saved.Sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') ||
            saved.SavedAt is null)
            throw new ArgumentException("Invalid checkpoint identity, sequence, fence, size or digest.", nameof(saved));
        DateTimeOffset at;
        try { at = saved.SavedAt.ToDateTimeOffset(); }
        catch (InvalidOperationException error) { throw new ArgumentException("Invalid checkpoint timestamp.", nameof(saved), error); }
        if (at < DateTimeOffset.UnixEpoch)
            throw new ArgumentException("Invalid checkpoint timestamp.", nameof(saved));
        at = new(at.UtcTicks / 10 * 10, TimeSpan.Zero);
        var key = Key(run, $"{task.NodeKey}/{task.TaskKey}", checked((long)saved.CheckpointSequence), saved.Sha256);
        if (saved.BlobKey != key) throw new ArgumentException("Checkpoint object does not match its identity.", nameof(saved));
        return new(run, $"{task.NodeKey}/{task.TaskKey}", checked((short)task.Attempt),
            checked((long)saved.CheckpointSequence), at, key, saved.Sha256, checked((long)saved.SizeBytes), saved.ReservationEpoch);
    }

    public static string Key(Guid runId, string taskKey, long sequence, string sha256)
    {
        var parts = taskKey?.Split('/');
        if (runId.Version != 7 || parts is not { Length: 2 } || !ValidKey(parts[0]) || !ValidKey(parts[1]) ||
            sequence <= 0 || sha256 is not { Length: 64 } ||
            !sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            throw new ArgumentException("Checkpoint keys require a canonical run/task, sequence and digest.");
        var taskHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(taskKey!)));
        return $"checkpoints/{runId:D}/{taskHash}/{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{sha256}.json";
    }

    public static void ValidateState(ReadOnlyMemory<byte> state)
    {
        if (state.Length is 0 or > MaximumBytes)
            throw new ArgumentException("Checkpoint JSON must be between 1 byte and 1 MiB.", nameof(state));
        using var document = JsonDocument.Parse(state);
    }

    private static bool ValidKey(string key) => !string.IsNullOrWhiteSpace(key) && !key.Contains('/') &&
        !key.Any(char.IsControl) && Encoding.UTF8.GetByteCount(key) <= 512;
}
