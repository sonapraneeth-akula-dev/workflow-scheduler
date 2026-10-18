using Npgsql;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Schedules;

public sealed class ScheduleConfigMigrationsTests(ScheduleConfigDatabase database) : IClassFixture<ScheduleConfigDatabase>
{
    [Fact]
    public async Task MigrationsConfigStoresProvenanceAndLinksOnlyMatchingCodeSchedules()
    {
        var id = Guid.CreateVersion7();
        var workflow = $"team/type/{id:N}";
        await InsertConfig(id, workflow);
        await using var link = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,code_key,name,timezone,cron,created_by,updated_by,config_version_id)
            VALUES ($1,$2,0,'daily','daily','Etc/UTC','* * * * *','test','test',$1)
            RETURNING config_version_id
            """);
        link.Parameters.AddWithValue(id);
        link.Parameters.AddWithValue(workflow);
        Assert.Equal(id, await link.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await using var read = database.DataSource.CreateCommand("""
            SELECT source_path,git_commit,octet_length(content_sha256),git_author,registered_by,registered_at
            FROM schedule_config_versions WHERE id=$1
            """);
        read.Parameters.AddWithValue(id);
        await using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("teams/demo/type/workflow/schedules.json", reader.GetString(0));
        Assert.Equal(new string('a', 40), reader.GetString(1));
        Assert.Equal(32, reader.GetInt32(2));
        Assert.Equal("author", reader.GetString(3));
        Assert.Equal("registrar", reader.GetString(4));
        Assert.Equal(DateTimeKind.Utc, reader.GetDateTime(5).Kind);
    }

    [Theory]
    [InlineData("content_sha256=decode(repeat('aa',31),'hex')")]
    [InlineData("content_sha256=decode(repeat('aa',33),'hex')")]
    [InlineData("git_commit='not-a-git-sha'")]
    [InlineData("source_path=' '")]
    [InlineData("git_author=''")]
    [InlineData("registered_by=''")]
    public async Task MigrationsConfigRejectsInvalidHashOrProvenance(string mutation)
    {
        var id = Guid.CreateVersion7();
        await InsertConfig(id, $"team/type/{id:N}");
        await using var change = database.DataSource.CreateCommand(
            $"UPDATE schedule_config_versions SET {mutation} WHERE id=$1");
        change.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task MigrationsConfigSameContentIsIdempotentlyAddressedWithinWorkflow()
    {
        var first = Guid.CreateVersion7();
        var workflow = $"team/type/{first:N}";
        await InsertConfig(first, workflow);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertConfig(Guid.CreateVersion7(), workflow));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        await InsertConfig(Guid.CreateVersion7(), workflow + "-other");
    }

    [Theory]
    [InlineData(true, false, PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(false, true, PostgresErrorCodes.CheckViolation)]
    public async Task MigrationsConfigRejectsCrossWorkflowOrUiLinks(bool wrongWorkflow, bool ui, string sqlState)
    {
        var id = Guid.CreateVersion7();
        var workflow = $"team/type/{id:N}";
        await InsertConfig(id, workflow);
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,code_key,name,timezone,interval_s,created_by,updated_by,
                config_version_id)
            VALUES ($1,$2,$3,$4,'interval','Etc/UTC',60,'test','test',$1)
            """);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(wrongWorkflow ? workflow + "-foreign" : workflow);
        insert.Parameters.AddWithValue((short)(ui ? 1 : 0));
        insert.Parameters.Add(new NpgsqlParameter
            { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = ui ? DBNull.Value : "stable" });
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(sqlState, error.SqlState);
    }

    [Fact]
    public async Task MigrationsConfigProvenanceCannotBeDeletedWhileReferenced()
    {
        var id = Guid.CreateVersion7();
        var workflow = $"team/type/{id:N}";
        await InsertConfig(id, workflow);
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,code_key,name,timezone,cron,created_by,updated_by,config_version_id)
            VALUES ($1,$2,0,'daily','daily','Etc/UTC','* * * * *','test','test',$1)
            """);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(workflow);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var delete = database.DataSource.CreateCommand("DELETE FROM schedule_config_versions WHERE id=$1");
        delete.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    private async Task InsertConfig(Guid id, string workflow)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO schedule_config_versions (id,workflow_id,source_path,git_commit,content_sha256,git_author,registered_by)
            VALUES ($1,$2,'teams/demo/type/workflow/schedules.json',repeat('a',40),decode(repeat('bb',32),'hex'),
                'author','registrar')
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(workflow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class ScheduleConfigUpgradeMigrationsTests(ScheduleConfigPreviousDatabase database) :
    IClassFixture<ScheduleConfigPreviousDatabase>
{
    [Fact]
    public async Task MigrationsConfigUpgradePreservesExistingCodeAndUiSchedules()
    {
        var code = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO schedules (id,workflow_id,origin,code_key,name,timezone,interval_s,created_by,updated_by)
            VALUES ($1,'team/type/legacy',0,'stable','code','Etc/UTC',60,'test','test'),
                   ($2,'team/type/legacy',1,NULL,'user','Etc/UTC',120,'test','test')
            """);
        seed.Parameters.AddWithValue(code);
        seed.Parameters.AddWithValue(user);
        await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations",
            "0008_schedule_config_versions.sql"), TestContext.Current.CancellationToken);
        await using var migrate = database.DataSource.CreateCommand(sql);
        await migrate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var read = database.DataSource.CreateCommand("""
            SELECT id,code_key,interval_s,row_version,config_version_id FROM schedules ORDER BY interval_s
            """);
        await using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(code, reader.GetGuid(0));
        Assert.Equal("stable", reader.GetString(1));
        Assert.Equal(60, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(user, reader.GetGuid(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(120, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.True(reader.IsDBNull(4));
    }
}

public sealed class ScheduleConfigDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => ["0007_schedules.sql", "0008_schedule_config_versions.sql"];
}

public sealed class ScheduleConfigPreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql", "0007_schedules.sql"];
}
