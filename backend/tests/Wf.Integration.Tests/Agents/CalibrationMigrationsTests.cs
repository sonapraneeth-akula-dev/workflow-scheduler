using Npgsql;

namespace Wf.Integration.Tests.Agents;

public sealed class CalibrationMigrationsTests(AgentDatabase database) : IClassFixture<AgentDatabase>
{
    [Fact]
    public async Task Calibration_upgrade_preserves_legacy_agent_and_defaults_capability_false()
    {
        var token = TestContext.Current.CancellationToken;
        var schema = "calibration_upgrade_" + Guid.NewGuid().ToString("N");
        await using var connection = await database.DataSource.OpenConnectionAsync(token);
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}; SET search_path TO {schema}", connection))
            await create.ExecuteNonQueryAsync(token);
        try
        {
            var previous = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0005_agents.sql"), token);
            await using (var old = new NpgsqlCommand(previous, connection)) await old.ExecuteNonQueryAsync(token);
            var id = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO agents(id,name,os,arch,cores,ram_mb,max_tasks,labels,pool,status,last_heartbeat_at,agent_version,session_id)
                VALUES($1,'legacy',1,0,4,8192,2,ARRAY['region=eu'],'original',0,now(),'1.0.0',$1)
                """, connection))
            {
                seed.Parameters.AddWithValue(id);
                await seed.ExecuteNonQueryAsync(token);
            }
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0035_calibration_commands.sql"), token);
            await using (var upgrade = new NpgsqlCommand(migration, connection)) await upgrade.ExecuteNonQueryAsync(token);
            await using var query = new NpgsqlCommand("""
                SELECT name,labels,pool,cores,ram_mb,supports_calibration_commands,
                    (SELECT count(*) FROM calibration_requests) FROM agents WHERE id=$1
                """, connection);
            query.Parameters.AddWithValue(id);
            await using var reader = await query.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal("legacy", reader.GetString(0));
            Assert.Equal(["region=eu"], reader.GetFieldValue<string[]>(1));
            Assert.Equal("original", reader.GetString(2));
            Assert.Equal(4, reader.GetInt32(3));
            Assert.Equal(8192, reader.GetInt32(4));
            Assert.False(reader.GetBoolean(5));
            Assert.Equal(0L, reader.GetInt64(6));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync(token);
        }
    }

    [Theory]
    [InlineData("epoch=1")]
    [InlineData("profile_key=''")]
    [InlineData("profile_key=repeat('a',2001)")]
    [InlineData("requested_actor=''")]
    [InlineData("requested_actor_kind='Engine'")]
    [InlineData("deadline_at=created_at")]
    [InlineData("status=4")]
    [InlineData("reported_rps=0")]
    [InlineData("status=2")]
    [InlineData("status=3")]
    [InlineData("applied=true")]
    public async Task Calibration_rows_reject_invalid_or_success_shaped_pending_state(string mutation)
    {
        var token = TestContext.Current.CancellationToken;
        var agent = await CapacityMigrationsTests.SeedAgent(database.DataSource);
        var id = Guid.CreateVersion7();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO calibration_requests(id,agent_id,profile_key,epoch,requested_actor,requested_actor_kind,created_at,deadline_at)
            VALUES($1,$2,$3,2,'principal','User',now(),now()+interval '2 minutes')
            """))
        {
            seed.Parameters.AddWithValue(id);
            seed.Parameters.AddWithValue(agent);
            seed.Parameters.AddWithValue(new Contracts.CapacityProfile("http", "json", "small").ProfileKey);
            await seed.ExecuteNonQueryAsync(token);
        }
        await using var update = database.DataSource.CreateCommand($"UPDATE calibration_requests SET {mutation} WHERE id=$1");
        update.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }
}
