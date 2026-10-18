using System.Globalization;
using Google.Protobuf;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Logs;

public static class LiveLogProtocol
{
    public const int MaximumRecords = 256;
    public const int MaximumBatchBytes = 512 * 1024;
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan InterestLifetime = TimeSpan.FromSeconds(15);

    public static string Identity(AttemptRef task)
    {
        _ = LogIndex.PrefixFor(task);
        if (task.NodeKey.Any(char.IsControl) || task.TaskKey.Any(char.IsControl))
            throw new ArgumentException("Log identity cannot contain control characters.");
        return $"{Guid.Parse(task.RunId):D}/{task.NodeKey}/{task.TaskKey}/{task.Attempt.ToString(CultureInfo.InvariantCulture)}";
    }

    public static AttemptRef Identity(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var parts = id.Split('/');
        if (parts.Length != 4 || !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var attempt))
            throw new ArgumentException("Invalid live log identity.");
        var task = new AttemptRef { RunId = parts[0], NodeKey = parts[1], TaskKey = parts[2], Attempt = attempt };
        if (Identity(task) != id) throw new ArgumentException("Live log identity must be canonical.");
        return task;
    }

    public static void Validate(MirrorLogs command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = Identity(command.Task ?? throw new ArgumentException("Missing log attempt."));
        if (command.ReservationEpoch is 0 or > long.MaxValue || command.ExpiresAt is null)
            throw new ArgumentException("Log mirroring requires a reservation epoch and expiry.");
        DateTimeOffset expires;
        try { expires = command.ExpiresAt.ToDateTimeOffset(); }
        catch (InvalidOperationException error) { throw new ArgumentException("Invalid log interest expiry.", error); }
        if (expires > now + InterestLifetime)
            throw new ArgumentException("Log interest must expire within fifteen seconds.");
    }

    public static void Validate(LiveLogBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _ = Identity(batch.Task ?? throw new ArgumentException("Missing log attempt."));
        if (batch.ReservationEpoch is 0 or > long.MaxValue || batch.Records.Count is 0 or > MaximumRecords ||
            batch.CalculateSize() > MaximumBatchBytes)
            throw new ArgumentException("Live log batch exceeds its protocol bounds.");
        ulong previous = 0;
        foreach (var record in batch.Records)
        {
            if (record.Line is 0 or > long.MaxValue || previous != 0 && record.Line != previous + 1 ||
                record.At is null || record.Text.Length > 64 * 1024 ||
                record.Stream is not ("stdout" or "stderr" or "ctx") ||
                record.Level is not ("Information" or "Warning" or "Error" or "Debug"))
                throw new ArgumentException("Invalid live log record.");
            try { _ = record.At.ToDateTimeOffset(); }
            catch (InvalidOperationException error) { throw new ArgumentException("Invalid live log timestamp.", error); }
            previous = record.Line;
        }
    }
}
