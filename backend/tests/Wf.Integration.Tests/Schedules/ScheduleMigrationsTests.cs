using Npgsql;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Schedules;

public sealed class ScheduleEmptyMigrationsTests(ScheduleEmptyDatabase database) : IClassFixture<ScheduleEmptyDatabase>
{
    [Fact]
    public async Task MigrationsEmptyDatabaseStoresBothOriginsAndDefaultPolicies()
    {
        var id = Guid.CreateVersion7();
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,name,timezone,cron,created_by,updated_by)
            VALUES ($1,'team/release/deploy',1,'daily','America/New_York','0 9 * * *','operator','operator')
            RETURNING origin,code_key,overlap_policy,catchup_policy,paused,row_version,variables::text
            """);
        insert.Parameters.AddWithValue(id);
        await using var reader = await insert.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt16(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(0, reader.GetInt16(2));
        Assert.Equal(1, reader.GetInt16(3));
        Assert.False(reader.GetBoolean(4));
        Assert.Equal(1, reader.GetInt32(5));
        Assert.Equal("{}", reader.GetString(6));
        await reader.DisposeAsync();
        await using var code = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,code_key,name,timezone,interval_s,version_pin,
                overlap_policy,catchup_policy,variables,created_by,updated_by)
            VALUES ($1,'team/release/deploy',0,'smoke-'||$1,'smoke','Etc/UTC',60,'1.0.0',2,0,
                '{"env":"dev"}','registration','registration')
            RETURNING interval_s
            """);
        code.Parameters.AddWithValue(Guid.CreateVersion7());
        Assert.Equal(60, await code.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("cron=NULL", PostgresErrorCodes.CheckViolation)]
    [InlineData("interval_s=60", PostgresErrorCodes.CheckViolation)]
    [InlineData("cron=NULL,interval_s=0", PostgresErrorCodes.CheckViolation)]
    [InlineData("cron=NULL,interval_s=-1", PostgresErrorCodes.CheckViolation)]
    [InlineData("timezone=NULL", PostgresErrorCodes.NotNullViolation)]
    [InlineData("timezone='  '", PostgresErrorCodes.CheckViolation)]
    [InlineData("origin=2", PostgresErrorCodes.CheckViolation)]
    [InlineData("origin=0", PostgresErrorCodes.CheckViolation)]
    [InlineData("code_key='foreign-code'", PostgresErrorCodes.CheckViolation)]
    [InlineData("overlap_policy=3", PostgresErrorCodes.CheckViolation)]
    [InlineData("catchup_policy=2", PostgresErrorCodes.CheckViolation)]
    [InlineData("row_version=0", PostgresErrorCodes.CheckViolation)]
    [InlineData("variables='[]'::jsonb", PostgresErrorCodes.CheckViolation)]
    [InlineData("name=E'\\t'", PostgresErrorCodes.CheckViolation)]
    [InlineData("version_pin=' '", PostgresErrorCodes.CheckViolation)]
    public async Task MigrationsRejectInvalidScheduleRows(string mutation, string sqlState)
    {
        var id = Guid.CreateVersion7();
        await using var insert = ValidUser(id);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var update = database.DataSource.CreateCommand($"UPDATE schedules SET {mutation} WHERE id=$1");
        update.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(sqlState, error.SqlState);
    }

    [Fact]
    public async Task MigrationsCodeIdentityIsUniqueWithinWorkflowEvenAfterSoftDelete()
    {
        var workflow = $"team/type/{Guid.NewGuid():N}";
        await using var code = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,code_key,name,timezone,cron,created_by,updated_by,deleted_at)
            VALUES ($1,$2,0,'stable','daily','Etc/UTC','* * * * *','test','test',now())
            """);
        code.Parameters.AddWithValue(Guid.CreateVersion7());
        code.Parameters.AddWithValue(workflow);
        await code.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        code.Parameters[0].Value = Guid.CreateVersion7();
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            code.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        code.Parameters[1].Value = workflow + "-other";
        await code.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MigrationsOptimisticVersionAllowsExactlyOneConcurrentEditor()
    {
        var id = Guid.CreateVersion7();
        await using var insert = ValidUser(id);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        async Task<int> Edit()
        {
            await using var command = database.DataSource.CreateCommand("""
                UPDATE schedules SET name='edited', row_version=row_version+1, updated_at=now()
                WHERE id=$1 AND row_version=1
                """);
            command.Parameters.AddWithValue(id);
            return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var results = await Task.WhenAll(Edit(), Edit());
        Assert.Equal([0, 1], results.Order());
        await using var select = database.DataSource.CreateCommand("SELECT row_version FROM schedules WHERE id=$1");
        select.Parameters.AddWithValue(id);
        Assert.Equal(2, await select.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MigrationsDueIndexExcludesPausedAndDeletedSchedules()
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT indexdef FROM pg_indexes WHERE schemaname=current_schema() AND indexname='schedules_next_fire'
            """);
        var index = Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Contains("(next_fire_at, id)", index);
        Assert.Contains("NOT paused", index);
        Assert.Contains("deleted_at IS NULL", index);
    }

    private NpgsqlCommand ValidUser(Guid id)
    {
        var command = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,name,timezone,cron,created_by,updated_by)
            VALUES ($1,'team/type/workflow',1,'daily','Etc/UTC','* * * * *','test','test')
            """);
        command.Parameters.AddWithValue(id);
        return command;
    }
}

public sealed class ScheduleUpgradeMigrationsTests(SchedulePreviousDatabase database) : IClassFixture<SchedulePreviousDatabase>
{
    [Fact]
    public async Task MigrationsUpgradePreservesPreviousSchemaRowsAndQueries()
    {
        var id = Guid.CreateVersion7();
        await using var seed = database.DataSource.CreateCommand("""
            WITH inserted AS (
                INSERT INTO runs (run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,
                    created_at,last_seq,deadline_at)
                VALUES (uuid_v7_run_day($1),$1,'team/type/legacy',$1,0,'legacy','{}','{}',1,now(),1,
                    now()+interval '24 hours')
                RETURNING run_day,id
            )
            INSERT INTO run_events (run_day,run_id,seq,ts,kind,payload)
            SELECT run_day,id,1,now(),0,'legacy'::bytea FROM inserted
            """);
        seed.Parameters.AddWithValue(id);
        await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0007_schedules.sql"),
            TestContext.Current.CancellationToken);
        await using var migrate = database.DataSource.CreateCommand(sql);
        await migrate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var select = database.DataSource.CreateCommand("""
            SELECT r.workflow_id,e.payload,r.status,r.last_seq
            FROM runs r JOIN run_events e ON e.run_day=r.run_day AND e.run_id=r.id
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1 AND e.seq=1
            """);
        select.Parameters.AddWithValue(id);
        await using var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("team/type/legacy", reader.GetString(0));
        Assert.Equal("legacy"u8.ToArray(), reader.GetFieldValue<byte[]>(1));
        Assert.Equal(1, reader.GetInt16(2));
        Assert.Equal(1, reader.GetInt64(3));
        await reader.DisposeAsync();
        await using var tables = database.DataSource.CreateCommand("""
            SELECT count(*) FROM information_schema.tables WHERE table_schema=current_schema()
                AND table_name IN ('agents','task_attempts','dispatch_outbox','schedules')
            """);
        Assert.Equal(4L, await tables.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}

public sealed class ScheduleEmptyDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => ["0007_schedules.sql"];
}

public sealed class SchedulePreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql"];
}
