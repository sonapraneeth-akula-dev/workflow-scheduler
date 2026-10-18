using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Wf.Contracts;
using Wf.Engine.Core;
using Wf.Engine.Core.Rerun;

namespace Wf.ControlPlane.Engine.Recovery;

public sealed record TriggerRunCommand(RunSnapshot InitialState, string WorkflowId, Guid VersionId, DateTimeOffset DeadlineAt,
    Guid? SeriesId = null, Guid? ParentRunId = null, ImmutableDictionary<string, ReusedNode>? ReusedNodes = null);

public sealed class CommandInbox(NpgsqlDataSource dataSource, TimeProvider time)
{
    private static readonly RecoveryJson StrictJson = new(new(RecoveryJson.Default.Options)
    {
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    });

    public Task<Guid> TriggerAsync(TriggerRunCommand command, string createdBy, CancellationToken cancellationToken = default)
        => TriggerAsync(command, createdBy, null, cancellationToken);

    internal Task<Guid> TriggerAsync(TriggerRunCommand command, string createdBy, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        if (command.DeadlineAt <= time.GetUtcNow())
            throw new ArgumentException("A trigger requires a future deadline.", nameof(command));
        return InsertAsync(command.InitialState.RunId, 0,
            JsonSerializer.Serialize(command, RecoveryJson.Default.TriggerRunCommand), createdBy, cancellationToken, transaction);
    }

    public Task<Guid> AbortAsync(Guid runId, AbortRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.At > time.GetUtcNow())
            throw new ArgumentException("Abort acceptance cannot be in the future.", nameof(record));
        return InsertAsync(runId, 1, JsonSerializer.Serialize(record, RecoveryJson.Default.AbortRecord),
            record.By, cancellationToken);
    }

    internal Task<Guid> RerunAsync(TriggerRunCommand command, string createdBy, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        Validate(command, rerun: true);
        if (command.DeadlineAt <= time.GetUtcNow())
            throw new ArgumentException("A rerun requires a future deadline.", nameof(command));
        return InsertAsync(command.InitialState.RunId, 0,
            JsonSerializer.Serialize(command, RecoveryJson.Default.TriggerRunCommand), createdBy, cancellationToken, transaction);
    }

    internal static TriggerRunCommand ReadInitial(string payload)
    {
        var command = JsonSerializer.Deserialize(payload, StrictJson.TriggerRunCommand)
            ?? throw new InvalidDataException("The initial run command is null.");
        Validate(command, rerun: command.ParentRunId is not null);
        return command;
    }

    internal static void Validate(TriggerRunCommand command, bool rerun = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.InitialState);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkflowId);
        var snapshot = command.InitialState;
        _ = RunState.Restore(snapshot, [], _ => { });
        if (snapshot.Status != RunStatus.Queued || snapshot.LastSequence != 0 || snapshot.Abort is not null ||
            snapshot.EndedAt is not null || snapshot.AbortTargets is { Length: > 0 } ||
            snapshot.PlacementAgentId is not null || !snapshot.TaskWaitReasons.IsEmpty ||
            snapshot.Nodes.Values.Any(state => state != ExecutionStatus.Pending && !(rerun && state == ExecutionStatus.Reused)) ||
            snapshot.Tasks.Values.Any(state => state != ExecutionStatus.Pending && !(rerun && state == ExecutionStatus.Reused)) ||
            command.VersionId == Guid.Empty ||
            command.DeadlineAt.Offset != TimeSpan.Zero || command.SeriesId == Guid.Empty)
            throw new ArgumentException("A trigger requires an initial queued snapshot, version and UTC deadline.", nameof(command));
        if (!rerun)
        {
            if (command.ParentRunId is not null || command.ReusedNodes is not null)
                throw new ArgumentException("A trigger cannot carry rerun provenance.", nameof(command));
            return;
        }
        if (command.ParentRunId is not { Version: 7 } parent || parent == snapshot.RunId ||
            command.ReusedNodes is not { } reused ||
            snapshot.Nodes.Count(pair => pair.Value == ExecutionStatus.Reused) != reused.Count ||
            reused.Any(pair => pair.Value is null || pair.Value.ReusedFromRunId != parent ||
                pair.Value.Outputs.ValueKind != JsonValueKind.Object ||
                snapshot.Nodes.GetValueOrDefault(pair.Key) != ExecutionStatus.Reused) ||
            snapshot.Tasks.Any(pair => pair.Key.IndexOf('/') <= 0 ||
                !snapshot.Nodes.TryGetValue(pair.Key[..pair.Key.IndexOf('/')], out var node) || node != pair.Value))
            throw new ArgumentException("A rerun requires matching parent and reused-node provenance.", nameof(command));
    }

    private async Task<Guid> InsertAsync(Guid runId, short kind, string payload, string createdBy, CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        if (runId == Guid.Empty || runId.Version != 7)
            throw new ArgumentException("A command requires a preallocated UUIDv7 run ID.", nameof(runId));
        if (string.IsNullOrWhiteSpace(createdBy) || createdBy.Length > 200)
            throw new ArgumentException("A command requires a principal of at most 200 characters.", nameof(createdBy));
        const string sql = """
            WITH inserted AS (
                INSERT INTO commands(id,kind,run_id,payload,created_by,created_at)
                VALUES($1,$2,$3,$4::jsonb,$5,$6)
                ON CONFLICT(run_id,kind) DO UPDATE SET run_id=commands.run_id RETURNING id,run_id)
            SELECT id,pg_notify('commands',run_id::text) FROM inserted
            """;
        await using var command = transaction is null ? dataSource.CreateCommand(sql)
            : new NpgsqlCommand(sql, transaction.Connection ?? throw new InvalidOperationException("The command transaction is closed."), transaction);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(payload);
        command.Parameters.AddWithValue(createdBy);
        command.Parameters.AddWithValue(time.GetUtcNow());
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Command insertion returned no identity."));
    }
}

[JsonSerializable(typeof(TriggerRunCommand))]
[JsonSerializable(typeof(AbortRecord))]
internal sealed partial class RecoveryJson : JsonSerializerContext;
