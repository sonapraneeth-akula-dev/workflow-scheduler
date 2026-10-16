using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Journal;

/// <summary>
/// Single-writer, atomically replaced result snapshot. Failed writes never publish new in-memory state.
/// Existing snapshots fail closed on corruption, ownership mismatch or capacity errors; they are never reset.
/// </summary>
public sealed class ResultJournal : IDisposable
{
    public const int MaximumRecordBytes = 1024 * 1024;
    public const int MaximumRecords = 4096;
    public const int MaximumJournalBytes = 32 * 1024 * 1024;
    private const uint LegacyMagic = 0x314a4657;
    private const uint Magic = 0x324a4657;
    private readonly string path;
    private readonly Guid agentId;
    private readonly FileStream ownership;
    private bool disposed;
    internal ulong Sequence { get; private set; }
    internal ulong LeaderEpoch { get; private set; }
    internal ulong ReservationEpoch { get; private set; } = 1;
    internal IReadOnlyList<AgentMessage> Pending { get; private set; } = [];

    public ResultJournal(string directory, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Guid.TryParse(agentId, out this.agentId) || this.agentId == Guid.Empty)
            throw new ArgumentException("Journal requires a stable agent UUID.", nameof(agentId));
        var root = Path.GetFullPath(directory);
        RejectLinks(root);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Agent journal requires a Windows user identity.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        path = Path.Combine(root, "results.wfj");
        var lockPath = Path.Combine(root, "writer.lock");
        RejectLinks(lockPath);
        ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            RejectLinks(path);
            if (File.Exists(path)) Load();
            else Save(0, 0, []);
        }
        catch { ownership.Dispose(); throw; }
    }

    internal void Save(ulong sequence, ulong leaderEpoch, IReadOnlyList<AgentMessage> pending, ulong reservationEpoch = 1)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pending.Count > MaximumRecords) throw new IOException("Result journal record capacity exhausted; results were not discarded.");
        if (reservationEpoch == 0) throw new InvalidDataException("Result journal requires a positive machine fence.");
        using var buffer = new MemoryStream();
        // WFJ2 adds the durable machine fence; WFJ1 remains readable.
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(agentId.ToByteArray());
            writer.Write(sequence);
            writer.Write(leaderEpoch);
            writer.Write(reservationEpoch);
            writer.Write(pending.Count);
            ulong previous = 1;
            foreach (var message in pending)
            {
                Validate(message);
                if (message.Sequence <= previous || message.Sequence > sequence)
                    throw new InvalidDataException("Invalid result journal sequence order.");
                previous = message.Sequence;
                var bytes = message.ToByteArray();
                if (buffer.Length + bytes.Length + sizeof(int) + 32 > MaximumJournalBytes)
                    throw new IOException("Result journal byte capacity exhausted; results were not discarded.");
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }
        var bytesToWrite = buffer.ToArray();
        var next = path + ".next";
        RejectLinks(path);
        RejectLinks(next);
        using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(next, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(bytesToWrite);
            stream.Write(SHA256.HashData(bytesToWrite));
            stream.Flush(flushToDisk: true);
        }
        File.Move(next, path, overwrite: true);
        Sequence = sequence;
        LeaderEpoch = leaderEpoch;
        ReservationEpoch = reservationEpoch;
        Pending = pending.Select(message => message.Clone()).ToArray();
    }

    private void Load()
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 72 or > MaximumJournalBytes) throw new InvalidDataException("Invalid result journal size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)), bytes.AsSpan(bytes.Length - 32)))
            throw new InvalidDataException("Result journal checksum mismatch; refusing to discard results.");
        using var buffer = new MemoryStream(bytes, 0, bytes.Length - 32, writable: false);
        using var reader = new BinaryReader(buffer);
        try
        {
            var format = reader.ReadUInt32();
            if (format is not (Magic or LegacyMagic) || new Guid(reader.ReadBytes(16)) != agentId)
                throw new InvalidDataException("Result journal format or agent identity mismatch.");
            Sequence = reader.ReadUInt64();
            LeaderEpoch = reader.ReadUInt64();
            ReservationEpoch = format == Magic ? reader.ReadUInt64() : 1;
            if (ReservationEpoch == 0) throw new InvalidDataException("Invalid journal machine fence.");
            var count = reader.ReadInt32();
            if (count is < 0 or > MaximumRecords) throw new InvalidDataException("Invalid result journal record count.");
            var pending = new List<AgentMessage>(count);
            ulong previous = 1;
            for (var index = 0; index < count; index++)
            {
                var length = reader.ReadInt32();
                if (length is <= 0 or > MaximumRecordBytes || length > buffer.Length - buffer.Position)
                    throw new InvalidDataException("Invalid result journal record length.");
                var message = AgentMessage.Parser.ParseFrom(reader.ReadBytes(length));
                Validate(message);
                if (message.Sequence <= previous || message.Sequence > Sequence)
                    throw new InvalidDataException("Invalid result journal sequence order.");
                previous = message.Sequence;
                pending.Add(message);
            }
            if (buffer.Position != buffer.Length) throw new InvalidDataException("Trailing result journal data.");
            Pending = pending;
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidProtocolBufferException or ArgumentException)
        {
            throw new InvalidDataException("Malformed result journal; refusing to discard results.", exception);
        }
    }

    internal static void Validate(AgentMessage message)
    {
        if (message.PayloadCase is not (AgentMessage.PayloadOneofCase.TaskStarted or AgentMessage.PayloadOneofCase.TaskCompleted or
            AgentMessage.PayloadOneofCase.ResourceSample or AgentMessage.PayloadOneofCase.ReservationFenced) ||
            message.CalculateSize() > MaximumRecordBytes)
            throw new InvalidDataException("Journal accepts only bounded task events.");
        if (message.ReservationFenced is { } fenced)
        {
            if (fenced.ReservationEpoch == 0) throw new InvalidDataException("Fence acknowledgement requires a positive epoch.");
            return;
        }
        var task = message.TaskStarted?.Task ?? message.TaskCompleted?.Task ?? message.ResourceSample?.Task;
        if (task is null || !Guid.TryParse(task.RunId, out var runId) || runId == Guid.Empty ||
            string.IsNullOrWhiteSpace(task.NodeKey) || task.NodeKey.Contains('/') ||
            string.IsNullOrWhiteSpace(task.TaskKey) || task.TaskKey.Contains('/') ||
            Encoding.UTF8.GetByteCount(task.NodeKey) > 512 || Encoding.UTF8.GetByteCount(task.TaskKey) > 512 ||
            task.Attempt is 0 or > 32767)
            throw new InvalidDataException("Journal task event requires a valid attempt.");
        if (message.ResourceSample is { } sample)
        {
            if (sample.ReservationEpoch == 0 || sample.SampleSequence is 0 or > long.MaxValue)
                throw new InvalidDataException("Resource sample requires a reservation and ordinal.");
            _ = Wf.Contracts.Resources.ResourceContract.Parse(sample.SampleJson.Memory);
            return;
        }
        var timestamp = message.TaskStarted?.At ?? message.TaskCompleted?.At;
        if (timestamp is null) throw new InvalidDataException("Journal task event requires a timestamp.");
        try { _ = timestamp.ToDateTimeOffset(); }
        catch (InvalidOperationException exception) { throw new InvalidDataException("Invalid result timestamp.", exception); }
        if (message.TaskCompleted is not { } completed) return;
        if (completed.Outcome == TaskOutcome.Unspecified || !Enum.IsDefined(completed.Outcome) ||
            completed.OutputsJson.Length > 64 * 1024 || Encoding.UTF8.GetByteCount(completed.Error?.Message ?? "") > 4096)
            throw new InvalidDataException("Invalid result outcome or oversized result payload.");
        if (completed.OutputsJson.Length == 0) return;
        try
        {
            using var outputs = JsonDocument.Parse(completed.OutputsJson.Memory);
            if (outputs.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Task outputs must be a JSON object.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid task output JSON.", exception); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Result journal paths cannot contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose();
    }
}
