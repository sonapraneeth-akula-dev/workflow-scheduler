using Npgsql;

namespace Wf.Integration.Tests.Agents;

public sealed class CapacityMigrationsTests(CapacityDatabase database) : IClassFixture<CapacityDatabase>
{
    [Fact]
    public async Task Fresh_database_stores_independent_profiles_and_sources_per_agent()
    {
        var first = await SeedAgent(database.DataSource);
        var second = await SeedAgent(database.DataSource);
        var small = new Contracts.CapacityProfile("http", "json", "small").ProfileKey;
        var large = new Contracts.CapacityProfile("http", "json", "large").ProfileKey;
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO agent_capacities (agent_id,profile_key,capacity_rps,source,calibrated_at)
            VALUES ($1,$3,100,0,NULL),($1,$4,200,1,$5),($2,$3,300,0,NULL)
            """);
        insert.Parameters.AddWithValue(first);
        insert.Parameters.AddWithValue(second);
        insert.Parameters.AddWithValue(small);
        insert.Parameters.AddWithValue(large);
        insert.Parameters.AddWithValue(DateTimeOffset.UnixEpoch);
        Assert.Equal(3, await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await using var select = database.DataSource.CreateCommand("""
            SELECT capacity_rps,source,calibrated_at FROM agent_capacities
            WHERE agent_id=$1 ORDER BY capacity_rps
            """);
        select.Parameters.AddWithValue(first);
        await using var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(100, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt16(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(200, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt16(1));
        Assert.Equal(DateTime.UnixEpoch, reader.GetDateTime(2));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("capacity_rps=0", PostgresErrorCodes.CheckViolation)]
    [InlineData("capacity_rps=-1", PostgresErrorCodes.CheckViolation)]
    [InlineData("source=2", PostgresErrorCodes.CheckViolation)]
    [InlineData("source=1", PostgresErrorCodes.CheckViolation)]
    [InlineData("calibrated_at=now()", PostgresErrorCodes.CheckViolation)]
    [InlineData("profile_key=''", PostgresErrorCodes.CheckViolation)]
    [InlineData("profile_key=E' \\t\\r\\n'", PostgresErrorCodes.CheckViolation)]
    [InlineData("profile_key=repeat('a',2001)", PostgresErrorCodes.CheckViolation)]
    [InlineData("profile_key=repeat(chr(9731),667)", PostgresErrorCodes.CheckViolation)]
    [InlineData("agent_id=gen_random_uuid()", PostgresErrorCodes.ForeignKeyViolation)]
    public async Task Invalid_capacity_rows_are_rejected(string mutation, string sqlState)
    {
        var id = await SeedAgent(database.DataSource);
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO agent_capacities (agent_id,profile_key,capacity_rps,source) VALUES ($1,'profile',1,0)
            """);
        insert.Parameters.AddWithValue(id);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var update = database.DataSource.CreateCommand($"UPDATE agent_capacities SET {mutation} WHERE agent_id=$1");
        update.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(sqlState, error.SqlState);
    }

    [Fact]
    public async Task Composite_identity_is_unique_and_agent_removal_releases_its_capacities()
    {
        var id = await SeedAgent(database.DataSource);
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO agent_capacities (agent_id,profile_key,capacity_rps,source) VALUES ($1,$2,1,0)
            """);
        insert.Parameters.AddWithValue(id);
        var settings = System.Collections.Immutable.ImmutableSortedDictionary<string, string>.Empty.Add("x", "");
        var prefix = new Contracts.CapacityProfile("http", "json", "small", settings).ProfileKey;
        insert.Parameters.AddWithValue(new Contracts.CapacityProfile("http", "json", "small",
            settings.SetItem("x", new string('a', 2000 - System.Text.Encoding.UTF8.GetByteCount(prefix)))).ProfileKey);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        await using var delete = database.DataSource.CreateCommand("DELETE FROM agents WHERE id=$1");
        delete.Parameters.AddWithValue(id);
        Assert.Equal(1, await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM agent_capacities WHERE agent_id=$1");
        count.Parameters.AddWithValue(id);
        Assert.Equal(0L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    internal static async Task<Guid> SeedAgent(NpgsqlDataSource source)
    {
        var id = Guid.NewGuid();
        await using var insert = source.CreateCommand("""
            INSERT INTO agents (id,name,os,arch,cores,ram_mb,max_tasks,labels,pool,status,last_heartbeat_at,agent_version,session_id)
            VALUES ($1,$1::text,1,0,4,8192,2,'{}','default',0,now(),'1.0',$1)
            """);
        insert.Parameters.AddWithValue(id);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }
}

public sealed class CapacityUpgradeMigrationsTests(CapacityPreviousDatabase database) : IClassFixture<CapacityPreviousDatabase>
{
    [Fact]
    public async Task Upgrade_preserves_registered_agent_and_accepts_capacity_without_rewriting_it()
    {
        var id = await CapacityMigrationsTests.SeedAgent(database.DataSource);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0012_agent_capacities.sql"),
            TestContext.Current.CancellationToken);
        await using var migration = database.DataSource.CreateCommand(sql);
        await migration.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO agent_capacities (agent_id,profile_key,capacity_rps,source)
            VALUES ($1,$2,150,0)
            """);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(new Contracts.CapacityProfile("http", "json", "small").ProfileKey);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var select = database.DataSource.CreateCommand("""
            SELECT a.name,a.cores,a.max_tasks,a.slots_used,c.capacity_rps
            FROM agents a JOIN agent_capacities c ON c.agent_id=a.id WHERE a.id=$1
            """);
        select.Parameters.AddWithValue(id);
        await using var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(id.ToString(), reader.GetString(0));
        Assert.Equal(4, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(150, reader.GetInt32(4));
    }
}

public sealed class CapacityDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => ["0005_agents.sql", "0012_agent_capacities.sql"];
}

public sealed class CapacityPreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql",
         "0007_schedules.sql", "0008_schedule_config_versions.sql", "0009_leases.sql", "0010_commands.sql",
         "0011_attempt_replacement.sql"];
}
