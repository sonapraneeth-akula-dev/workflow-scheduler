using Npgsql;
using Wf.ControlPlane.Data;
using Wf.Engine.Core.Scheduling;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Schedules;

public sealed class ScheduleFireMigrationsTests(ScheduleFireDatabase database) : IClassFixture<ScheduleFireDatabase>
{
    private static readonly DateTimeOffset FireAt = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ScheduleFireOutcome.Started, 0)]
    [InlineData(ScheduleFireOutcome.Skipped, 1)]
    [InlineData(ScheduleFireOutcome.Missed, 2)]
    [InlineData(ScheduleFireOutcome.CaughtUp, 3)]
    public async Task Every_documented_outcome_has_stable_storage_and_optional_run_and_reason(
        ScheduleFireOutcome outcome, short expected)
    {
        var schedule = await SeedSchedule();
        Assert.Equal(expected, (short)outcome);
        await using var insert = Claim(schedule, FireAt, outcome);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await using var read = database.DataSource.CreateCommand("""
            SELECT outcome,run_id,reason,fire_at FROM schedule_fires WHERE schedule_id=$1 AND fire_at=$2
            """);
        read.Parameters.AddWithValue(schedule);
        read.Parameters.AddWithValue(FireAt);
        await using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, reader.GetInt16(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(FireAt.UtcDateTime, reader.GetDateTime(3));
    }

    [Fact]
    public async Task Duplicate_slot_is_a_noop_even_with_another_offset_or_different_outcome()
    {
        var schedule = await SeedSchedule();
        await using var first = Claim(schedule, FireAt, ScheduleFireOutcome.Missed);
        Assert.Equal(1, await first.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await using var duplicate = Claim(schedule, FireAt.ToOffset(TimeSpan.FromHours(-7)), ScheduleFireOutcome.Started);
        Assert.Equal(0, await duplicate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await using var read = database.DataSource.CreateCommand("SELECT outcome FROM schedule_fires WHERE schedule_id=$1");
        read.Parameters.AddWithValue(schedule);
        Assert.Equal((short)ScheduleFireOutcome.Missed, await read.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_engines_can_claim_a_slot_only_once()
    {
        var schedule = await SeedSchedule();
        var winners = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var claim = Claim(schedule, FireAt, ScheduleFireOutcome.Started);
            return await claim.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }));
        Assert.Equal(1, winners.Sum());
        Assert.Equal(1, winners.Count(result => result == 1));
        Assert.Equal(15, winners.Count(result => result == 0));
    }

    [Fact]
    public async Task Distinct_schedules_and_adjacent_postgres_microsecond_slots_are_independent()
    {
        var first = await SeedSchedule();
        var second = await SeedSchedule();
        foreach (var (schedule, fire) in new[] { (first, FireAt), (second, FireAt), (first, FireAt.AddTicks(10)) })
        {
            await using var claim = Claim(schedule, fire, ScheduleFireOutcome.Started);
            Assert.Equal(1, await claim.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM schedule_fires WHERE schedule_id=$1 OR schedule_id=$2");
        count.Parameters.AddWithValue(first);
        count.Parameters.AddWithValue(second);
        Assert.Equal(3L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rolled_back_claim_can_be_retried_without_a_phantom_history_record()
    {
        var schedule = await SeedSchedule();
        await using (var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await using var claim = new NpgsqlCommand("""
                INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,$2,0)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            claim.Parameters.AddWithValue(schedule);
            claim.Parameters.AddWithValue(FireAt);
            Assert.Equal(1, await claim.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }
        await using var retry = Claim(schedule, FireAt, ScheduleFireOutcome.Started);
        Assert.Equal(1, await retry.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_schedule_is_rejected_and_existing_history_prevents_hard_delete()
    {
        await using var unknown = Claim(Guid.CreateVersion7(), FireAt, ScheduleFireOutcome.Started);
        var missing = await Assert.ThrowsAsync<PostgresException>(() =>
            unknown.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missing.SqlState);
        var schedule = await SeedSchedule();
        await using var claim = Claim(schedule, FireAt, ScheduleFireOutcome.Missed);
        await claim.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var delete = database.DataSource.CreateCommand("DELETE FROM schedules WHERE id=$1");
        delete.Parameters.AddWithValue(schedule);
        var retained = await Assert.ThrowsAsync<PostgresException>(() =>
            delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, retained.SqlState);
        await using var softDelete = database.DataSource.CreateCommand("UPDATE schedules SET deleted_at=now() WHERE id=$1");
        softDelete.Parameters.AddWithValue(schedule);
        Assert.Equal(1, await softDelete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("-1", "'2026-10-15T12:00:00Z'", PostgresErrorCodes.CheckViolation)]
    [InlineData("4", "'2026-10-15T12:00:00Z'", PostgresErrorCodes.CheckViolation)]
    [InlineData("NULL", "'2026-10-15T12:00:00Z'", PostgresErrorCodes.NotNullViolation)]
    [InlineData("0", "'infinity'", PostgresErrorCodes.CheckViolation)]
    [InlineData("0", "'-infinity'", PostgresErrorCodes.CheckViolation)]
    [InlineData("0", "NULL", PostgresErrorCodes.NotNullViolation)]
    public async Task Malformed_history_is_rejected(string outcome, string fire, string sqlState)
    {
        var schedule = await SeedSchedule();
        await using var command = database.DataSource.CreateCommand($"""
            INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,{fire},{outcome})
            """);
        command.Parameters.AddWithValue(schedule);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(sqlState, error.SqlState);
    }

    private async Task<Guid> SeedSchedule()
    {
        var id = Guid.CreateVersion7();
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
            VALUES($1,'team/type/workflow',1,'interval','Etc/UTC',60,'test','test')
            """);
        insert.Parameters.AddWithValue(id);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private NpgsqlCommand Claim(Guid schedule, DateTimeOffset fire, ScheduleFireOutcome outcome)
    {
        var command = database.DataSource.CreateCommand("""
            INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,$2,$3)
            ON CONFLICT (schedule_id,fire_at) DO NOTHING
            """);
        command.Parameters.AddWithValue(schedule);
        command.Parameters.AddWithValue(fire.ToUniversalTime());
        command.Parameters.AddWithValue((short)outcome);
        return command;
    }
}

public sealed class ScheduleFireUpgradeMigrationsTests(ScheduleFirePreviousDatabase database)
    : IClassFixture<ScheduleFirePreviousDatabase>
{
    [Fact]
    public async Task Upgrade_preserves_existing_run_event_schedule_and_supports_source_linked_catch_up()
    {
        var id = Guid.CreateVersion7();
        await using var seed = database.DataSource.CreateCommand("""
            WITH inserted AS (
                INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,last_seq)
                VALUES(uuid_v7_run_day(@id),@id,'team/type/legacy',@id,0,'legacy','{}','{}',0,now(),1)
                RETURNING run_day,id
            )
            INSERT INTO run_events(run_day,run_id,seq,ts,kind,payload)
            SELECT run_day,id,1,now(),0,'legacy'::bytea FROM inserted;
            INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by,row_version)
            VALUES(@id,'team/type/legacy',1,'interval','Etc/UTC',60,'legacy','legacy',3);
            """);
        seed.Parameters.AddWithValue("id", id);
        await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0014_schedule_fires.sql"),
            TestContext.Current.CancellationToken);
        await using var migrate = database.DataSource.CreateCommand(sql);
        await migrate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var history = database.DataSource.CreateCommand("""
            INSERT INTO schedule_fires(schedule_id,fire_at,outcome,run_id,reason)
            VALUES(@id,now(),2,@id,'latest-only catch-up');
            SELECT s.row_version,r.workflow_id,e.payload,f.outcome,f.run_id,f.reason
            FROM schedules s
            JOIN runs r ON r.id=s.id AND r.run_day=uuid_v7_run_day(s.id)
            JOIN run_events e ON e.run_day=r.run_day AND e.run_id=r.id AND e.seq=1
            JOIN schedule_fires f ON f.schedule_id=s.id
            WHERE s.id=@id
            """);
        history.Parameters.AddWithValue("id", id);
        await using var reader = await history.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal("team/type/legacy", reader.GetString(1));
        Assert.Equal("legacy"u8.ToArray(), reader.GetFieldValue<byte[]>(2));
        Assert.Equal((short)ScheduleFireOutcome.Missed, reader.GetInt16(3));
        Assert.Equal(id, reader.GetGuid(4));
        Assert.Equal("latest-only catch-up", reader.GetString(5));
        await reader.DisposeAsync();
        // History must outlive run retention without a partition-pinning run FK.
        await using var retain = database.DataSource.CreateCommand("""
            DELETE FROM run_events WHERE run_day=uuid_v7_run_day(@id) AND run_id=@id;
            DELETE FROM runs WHERE run_day=uuid_v7_run_day(@id) AND id=@id;
            SELECT run_id FROM schedule_fires WHERE schedule_id=@id
            """);
        retain.Parameters.AddWithValue("id", id);
        Assert.Equal(id, await retain.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}

public sealed class ScheduleFireDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => ["0007_schedules.sql", "0014_schedule_fires.sql"];
}

public sealed class ScheduleFirePreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts()
        .Where(script => string.CompareOrdinal(script.Name, "0014") < 0).Select(script => script.Name).ToArray();
}
