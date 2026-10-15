using Npgsql;
using Wf.ControlPlane.Data;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Schedules;

public sealed class ScheduleFileApplicationMigrationsTests(ScheduleApplicationPreviousDatabase database)
    : IClassFixture<ScheduleApplicationPreviousDatabase>
{
    [Fact]
    public async Task ScheduleFileApplier_migration_upgrade_backfills_series_and_preserves_legacy_schedules_and_configs()
    {
        var code = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var config = Guid.CreateVersion7();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO schedule_config_versions(id,workflow_id,source_path,git_commit,content_sha256,git_author,registered_by)
            VALUES($1,'legacy/type/workflow','schedules.json',repeat('a',40),decode(repeat('00',32),'hex'),'author','actor')
            """))
        {
            seed.Parameters.AddWithValue(config);
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO schedules(id,workflow_id,origin,code_key,name,timezone,interval_s,created_by,updated_by,config_version_id)
            VALUES($1,'legacy/type/workflow',0,'stable','code','Etc/UTC',60,'actor','actor',$3),
                  ($2,'legacy/type/workflow',1,NULL,'user','Etc/UTC',120,'actor','actor',NULL)
            """))
        {
            seed.Parameters.AddWithValue(code);
            seed.Parameters.AddWithValue(user);
            seed.Parameters.AddWithValue(config);
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var migration = Migrator.LoadScripts().Single(script => script.Name == "0018_schedule_file_application.sql");
        await using (var migrate = database.DataSource.CreateCommand(migration.Contents))
            await migrate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using (var query = database.DataSource.CreateCommand("""
            SELECT s.id,s.code_key,s.interval_s,s.row_version,s.config_version_id,r.schedule_id,r.workflow_id
            FROM schedules s JOIN run_series r ON r.schedule_id=s.id ORDER BY s.interval_s
            """))
        {
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(code, reader.GetGuid(0));
            Assert.Equal("stable", reader.GetString(1));
            Assert.Equal(60, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(config, reader.GetGuid(4));
            Assert.Equal(code, reader.GetGuid(5));
            Assert.Equal("legacy/type/workflow", reader.GetString(6));
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(user, reader.GetGuid(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(120, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.True(reader.IsDBNull(4));
            Assert.Equal(user, reader.GetGuid(5));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        await using var read = database.DataSource.CreateCommand("SELECT content IS NULL FROM schedule_config_versions WHERE id=$1");
        read.Parameters.AddWithValue(config);
        Assert.Equal(true, await read.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await using var duplicate = database.DataSource.CreateCommand("""
            INSERT INTO run_series(id,workflow_id,schedule_id) VALUES($1,'legacy/type/workflow',$2)
            """);
        duplicate.Parameters.AddWithValue(Guid.CreateVersion7());
        duplicate.Parameters.AddWithValue(code);
        var error = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }
}

public sealed class ScheduleApplicationPreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        Migrator.LoadScripts().Where(script => string.CompareOrdinal(script.Name, "0018") < 0)
            .Select(script => script.Name).ToArray();
}
