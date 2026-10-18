using System.Buffers.Binary;
using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Host.V1;

namespace Wf.Sdk.Hosting;

internal sealed class AttemptSpool
{
    internal const long MaximumBytes = 20_000_000_000;
    private readonly object gate = new();
    private readonly SortedDictionary<ulong, string> records = [];
    private readonly Dispatch dispatch;
    private TaskCompletionSource changed = Signal();
    private ulong sequence;
    private ulong acknowledged;
    private ulong resourceSequence;
    private long bytes;
    private bool complete;
    private bool recordsSealed;
    private readonly long maximumBytes;
    internal string DirectoryPath { get; }

    internal AttemptSpool(string attemptDirectory, Dispatch dispatch, long maximumBytes = MaximumBytes)
    {
        if (!Path.IsPathFullyQualified(attemptDirectory))
            throw new ArgumentException("Attempt spool requires an absolute owner-private directory.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.maximumBytes = maximumBytes;
        RejectLinks(attemptDirectory);
        DirectoryPath = Path.Combine(attemptDirectory, "spool");
        Directory.CreateDirectory(DirectoryPath);
        RejectLinks(DirectoryPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        this.dispatch = dispatch.Clone();
        var dispatchPath = Path.Combine(DirectoryPath, "dispatch.pb");
        if (File.Exists(dispatchPath))
        {
            if (!Dispatch.Parser.ParseFrom(Read(dispatchPath, 4 * 1024 * 1024)).Equals(dispatch))
                throw new InvalidDataException("Attempt spool belongs to another dispatch.");
        }
        else Write(dispatchPath, dispatch.ToByteArray());
        var statePath = Path.Combine(DirectoryPath, "ack");
        if (File.Exists(statePath))
        {
            var state = Read(statePath, 24);
            if (state.Length != 24) throw new InvalidDataException("Invalid attempt spool acknowledgement.");
            acknowledged = BinaryPrimitives.ReadUInt64LittleEndian(state);
            sequence = BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(8));
            resourceSequence = BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(16));
            if (acknowledged > sequence) throw new InvalidDataException("Invalid attempt spool cursor.");
        }
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.record"))
        {
            var record = AttemptRecord.Parser.ParseFrom(Read(file, 128 * 1024));
            Validate(record);
            if (Path.GetFileName(file) != Name(record.Sequence))
                throw new InvalidDataException("Attempt spool record name does not match its sequence.");
            sequence = Math.Max(sequence, record.Sequence);
            resourceSequence = Math.Max(resourceSequence, record.Resource?.SampleSequence ?? 0);
            if (record.Sequence <= acknowledged) File.Delete(file);
            else { records.Add(record.Sequence, file); bytes = checked(bytes + new FileInfo(file).Length); }
        }
        complete = File.Exists(Path.Combine(DirectoryPath, "result.pb"));
    }

    internal void Log(string stream, string text)
    {
        if (stream is not ("stdout" or "stderr") || text.Length > 64 * 1024)
            throw new ArgumentException("Invalid host log record.");
        lock (gate)
            Append(new AttemptRecord { Log = new HostLog
            {
                At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Stream = stream, Text = text
            } });
    }

    internal void Resource(ResourceSample sample)
    {
        if (!sample.Task.Equals(dispatch.Task) || sample.ReservationEpoch != dispatch.ReservationEpoch)
            throw new InvalidDataException("Resource sample does not match the bound attempt.");
        _ = Wf.Contracts.Resources.ResourceContract.Parse(sample.SampleJson.Memory);
        lock (gate)
        {
            if (recordsSealed) throw new InvalidOperationException("Attempt resource records have been sealed.");
            var copy = sample.Clone();
            copy.SampleSequence = checked(resourceSequence + 1);
            Append(new AttemptRecord { Resource = copy }, resource: true);
            resourceSequence = copy.SampleSequence;
        }
    }

    internal void Progress(TaskProgress update)
    {
        _ = Wf.Contracts.Progress.TaskProgressProtocol.Validate(update);
        if (!update.Task.Equals(dispatch.Task) || update.ReservationEpoch != dispatch.ReservationEpoch)
            throw new InvalidDataException("Task progress does not match the bound attempt.");
        lock (gate) Append(new AttemptRecord { Progress = update.Clone() });
    }

    private void Append(AttemptRecord record, bool resource = false)
    {
        if (recordsSealed || complete && !resource)
            throw new InvalidOperationException("Attempt spool has completed.");
        record.Sequence = checked(sequence + 1);
        record.Task = dispatch.Task.Clone();
        record.ReservationEpoch = dispatch.ReservationEpoch;
        var content = record.ToByteArray();
        if (content.Length > 128 * 1024 || bytes > maximumBytes - content.Length || records.Count >= 1_000_000)
            throw new IOException("Unacknowledged task-host spool quota exceeded.");
        var path = Path.Combine(DirectoryPath, Name(record.Sequence));
        Write(path, content);
        records.Add(record.Sequence, path);
        sequence = record.Sequence;
        bytes += content.Length;
        Notify();
    }

    internal (AttemptRecord[] Records, bool Complete, Task Changed) ReadAfter(ulong after)
    {
        lock (gate)
        {
            if (after > sequence) throw new InvalidDataException("Attempt spool cursor is ahead of the producer.");
            return (records.Where(pair => pair.Key > Math.Max(after, acknowledged)).Take(64)
                .Select(pair => AttemptRecord.Parser.ParseFrom(Read(pair.Value, 128 * 1024))).ToArray(), complete && recordsSealed, changed.Task);
        }
    }

    internal void Confirm(ulong through)
    {
        lock (gate)
        {
            if (through == 0 || through > sequence)
                throw new InvalidDataException("Invalid attempt spool acknowledgement.");
            if (through <= acknowledged) return;
            var state = new byte[24];
            BinaryPrimitives.WriteUInt64LittleEndian(state, through);
            BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(8), sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(16), resourceSequence);
            Write(Path.Combine(DirectoryPath, "ack"), state);
            acknowledged = through;
            foreach (var key in records.Keys.TakeWhile(key => key <= through).ToArray())
            {
                var path = records[key];
                bytes -= new FileInfo(path).Length;
                File.Delete(path);
                records.Remove(key);
            }
        }
    }

    internal TaskCompleted Complete(TaskCompleted result)
    {
        lock (gate)
        {
            if (!result.Task.Equals(dispatch.Task))
                throw new InvalidDataException("Task result does not match its spool.");
            var stored = SaveResult(DirectoryPath, result);
            complete = true;
            Notify();
            return stored;
        }
    }

    internal void RequireAcknowledged()
    {
        lock (gate)
            if (!complete || !recordsSealed || acknowledged != sequence)
                throw new InvalidOperationException("Attempt records have not all been durably acknowledged.");
    }

    internal void SealRecords()
    {
        lock (gate)
        {
            if (!complete) throw new InvalidOperationException("Task result must be durable before sealing its records.");
            recordsSealed = true;
            Notify();
        }
    }

    internal static TaskCompleted SaveResult(string directory, TaskCompleted result)
    {
        var path = Path.Combine(directory, "result.pb");
        try { Write(path, result.ToByteArray(), replace: false); }
        catch (IOException) when (File.Exists(path))
        { return TaskCompleted.Parser.ParseFrom(Read(path, 1024 * 1024)); }
        return result;
    }

    internal static void Write(string path, byte[] bytes, bool replace = true)
    {
        RejectLinks(path);
        var next = path + "." + Guid.NewGuid().ToString("N") + ".next";
        try
        {
            using (var file = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(next, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(next, path, replace);
        }
        finally { File.Delete(next); }
    }

    internal static byte[] Read(string path, int maximum)
    {
        RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length is <= 0 || file.Length > maximum)
            throw new InvalidDataException("Attempt spool file exceeds its size limit.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static void RejectLinks(string path)
    {
        for (var entry = new FileInfo(path); entry is not null; entry = entry.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((entry.Exists || Directory.Exists(entry.FullName)) &&
                (File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Attempt spool cannot traverse symbolic links or reparse points.");
    }

    private void Validate(AttemptRecord record)
    {
        if (record.Sequence == 0 || !record.Task.Equals(dispatch.Task) || record.ReservationEpoch != dispatch.ReservationEpoch ||
            record.PayloadCase is not (AttemptRecord.PayloadOneofCase.Log or AttemptRecord.PayloadOneofCase.Resource or
                AttemptRecord.PayloadOneofCase.Progress))
            throw new InvalidDataException("Invalid attempt spool record identity.");
    }

    private void Notify() { var previous = changed; changed = Signal(); previous.TrySetResult(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Name(ulong number) => number.ToString("D20", CultureInfo.InvariantCulture) + ".record";
}
