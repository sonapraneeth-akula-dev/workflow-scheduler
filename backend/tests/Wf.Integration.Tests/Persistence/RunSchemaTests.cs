using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Persistence;

public sealed class RunSchemaTests : IAsyncLifetime
{
    private PostgreSqlContainer? postgres;
    private string connectionString = "";
    private string? schema;

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
        if (external is null)
        {
            postgres = new PostgreSqlBuilder("postgres:18.6-alpine")
                .WithName($"kardia-test-run-schema-postgres-{Guid.NewGuid().ToString("N")[..12]}").Build();
            await postgres.StartAsync();
            connectionString = postgres.GetConnectionString();
        }
        else
        {
            schema = $"test_{Guid.NewGuid():N}";
            connectionString = new NpgsqlConnectionStringBuilder(external) { SearchPath = schema }.ConnectionString;
            await using var admin = new NpgsqlConnection(external);
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0002_runs_events.sql"));
        await using var command = new NpgsqlCommand(migration, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (postgres is not null)
            await postgres.DisposeAsync();
        else if (schema is not null)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task MigrationAcceptsV7RunEventsAndSnapshotsAndReplayMatchesLiveState()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7(now);
        var run = new RunState(id, [new("node", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        var initial = run.Snapshot;
        var @event = run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, now);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO runs (run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES (uuid_v7_run_day(@id),@id,'team/type/name',@version,0,'test','{}','{}',0,@now);
            INSERT INTO run_events (run_day,run_id,seq,ts,kind,payload)
            VALUES (uuid_v7_run_day(@id),@id,1,@now,0,@event);
            INSERT INTO run_snapshots (run_day,run_id,seq,created_at,payload)
            VALUES (uuid_v7_run_day(@id),@id,0,@now,@snapshot);
            UPDATE runs SET last_seq=1,status=1 WHERE run_day=uuid_v7_run_day(@id) AND id=@id;
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("version", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("now", now);
        insert.Parameters.AddWithValue("event", JsonSerializer.SerializeToUtf8Bytes(@event));
        insert.Parameters.AddWithValue("snapshot", JsonSerializer.SerializeToUtf8Bytes(initial));
        await insert.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        run.Apply(@event);
        await using var select = new NpgsqlCommand("""
            SELECT payload FROM run_events WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 ORDER BY seq
            """, connection);
        select.Parameters.AddWithValue(id);
        var payload = Assert.IsType<byte[]>(await select.ExecuteScalarAsync(cancellation));
        await using var snapshotQuery = new NpgsqlCommand("""
            SELECT payload FROM run_snapshots WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND seq=0
            """, connection);
        snapshotQuery.Parameters.AddWithValue(id);
        var snapshotBytes = Assert.IsType<byte[]>(await snapshotQuery.ExecuteScalarAsync(cancellation));
        var storedSnapshot = JsonSerializer.Deserialize<RunSnapshot>(snapshotBytes)!;
        var replay = RunState.Restore(storedSnapshot, [JsonSerializer.Deserialize<RunEvent>(payload)!], Assert.Fail);
        Assert.Equal(JsonSerializer.Serialize(run.Snapshot), JsonSerializer.Serialize(replay.Snapshot));
        Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), await RunDay(connection, id, cancellation));
        await using var duplicate = new NpgsqlCommand("""
            INSERT INTO run_events (run_day,run_id,seq,ts,kind,payload)
            VALUES (uuid_v7_run_day($1),$1,1,now(),0,''::bytea)
            """, connection);
        duplicate.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync(cancellation));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    [Fact]
    public async Task SchemaRejectsDuplicateSequenceAndInvalidPartitionKeyAndForeignRun()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        var id = Guid.CreateVersion7();
        await using var invalidDay = new NpgsqlCommand("""
            INSERT INTO runs (run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES (uuid_v7_run_day($1)+1,$1,'team/type/name',$1,0,'test','{}','{}',0,now())
            """, connection);
        invalidDay.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => invalidDay.ExecuteNonQueryAsync(cancellation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await using var orphan = new NpgsqlCommand("""
            INSERT INTO run_events (run_day,run_id,seq,ts,kind,payload)
            VALUES (uuid_v7_run_day($1),$1,1,now(),0,''::bytea)
            """, connection);
        orphan.Parameters.AddWithValue(id);
        error = await Assert.ThrowsAsync<PostgresException>(() => orphan.ExecuteNonQueryAsync(cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    private static async Task<object?> RunDay(NpgsqlConnection connection, Guid id, CancellationToken cancellation)
    {
        await using var command = new NpgsqlCommand("SELECT uuid_v7_run_day($1)", connection);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(cancellation);
    }

    [Fact]
    public async Task DeadlineMigrationPersistsAbsoluteTimersForRecovery()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0003_attempt_timers.sql"),
            cancellation);
        await using var migration = new NpgsqlCommand(sql, connection);
        await migration.ExecuteNonQueryAsync(cancellation);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7(now);
        var deadline = now.AddHours(1);
        var retry = now.AddSeconds(10);
        await using var command = new NpgsqlCommand("""
            INSERT INTO runs (run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,deadline_at)
            VALUES (uuid_v7_run_day(@id),@id,'team/type/name',@id,0,'test','{}','{}',1,@now,@deadline);
            INSERT INTO node_runs (run_day,run_id,node_key,status,deadline_at)
            VALUES (uuid_v7_run_day(@id),@id,'node',3,@deadline);
            INSERT INTO task_attempts (run_day,run_id,task_key,attempt,status,deadline_at,retry_at)
            VALUES (uuid_v7_run_day(@id),@id,'node/task',1,4,@deadline,@retry);
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("deadline", deadline);
        command.Parameters.AddWithValue("retry", retry);
        await command.ExecuteNonQueryAsync(cancellation);
        await using var read = new NpgsqlCommand("""
            SELECT deadline_at,retry_at FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
            """, connection);
        read.Parameters.AddWithValue(id);
        await using var reader = await read.ExecuteReaderAsync(cancellation);
        Assert.True(await reader.ReadAsync(cancellation));
        Assert.True((deadline - reader.GetFieldValue<DateTimeOffset>(0)).Duration() < TimeSpan.FromMicroseconds(1));
        Assert.True((retry - reader.GetFieldValue<DateTimeOffset>(1)).Duration() < TimeSpan.FromMicroseconds(1));
    }
}
