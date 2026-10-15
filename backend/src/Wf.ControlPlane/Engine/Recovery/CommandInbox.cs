using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Wf.Contracts;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Engine.Recovery;

public sealed record TriggerRunCommand(RunSnapshot InitialState, string WorkflowId, Guid VersionId, DateTimeOffset DeadlineAt);

public sealed class CommandInbox(NpgsqlDataSource dataSource, TimeProvider time)
{
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

    internal static void Validate(TriggerRunCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.InitialState);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkflowId);
        var snapshot = command.InitialState;
        _ = RunState.Restore(snapshot, [], _ => { });
        if (snapshot.Status != RunStatus.Queued || snapshot.LastSequence != 0 || snapshot.Abort is not null ||
            snapshot.EndedAt is not null || snapshot.Nodes.Values.Any(state => state != ExecutionStatus.Pending) ||
            snapshot.Tasks.Values.Any(state => state != ExecutionStatus.Pending) || command.VersionId == Guid.Empty ||
            command.DeadlineAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A trigger requires an initial queued snapshot, version and UTC deadline.", nameof(command));
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
