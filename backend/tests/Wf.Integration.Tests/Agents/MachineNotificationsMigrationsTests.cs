using Npgsql;
using Wf.Contracts;
using Wf.ControlPlane.Data;

namespace Wf.Integration.Tests.Agents;

public sealed class MachineNotificationsMigrationsTests(MachineNotificationsPreviousDatabase database)
    : IClassFixture<MachineNotificationsPreviousDatabase>
{
    [Fact]
    public async Task Machine_notification_upgrade_preserves_existing_inventory_and_publishes_only_changed_committed_rows()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await CapacityMigrationsTests.SeedAgent(database.DataSource);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0033_machine_notifications.sql"), token);
        await using (var migration = database.DataSource.CreateCommand(sql))
            await migration.ExecuteNonQueryAsync(token);
        await using (var verify = database.DataSource.CreateCommand("SELECT name,cores,pool FROM agents WHERE id=$1"))
        {
            verify.Parameters.AddWithValue(id);
            await using var reader = await verify.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(id.ToString("D"), reader.GetString(0));
            Assert.Equal(4, reader.GetInt32(1));
            Assert.Equal("default", reader.GetString(2));
        }
        await using var listener = await database.DataSource.OpenConnectionAsync(token);
        var received = new List<string>();
        listener.Notification += (_, notification) => received.Add(notification.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN live_machines", listener))
            await listen.ExecuteNonQueryAsync(token);
        await using (var connection = await database.DataSource.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            await using var change = new NpgsqlCommand("UPDATE agents SET pool='rolled-back' WHERE id=$1", connection, transaction);
            change.Parameters.AddWithValue(id);
            await change.ExecuteNonQueryAsync(token);
            await transaction.RollbackAsync(token);
        }
        await using (var unchanged = database.DataSource.CreateCommand("UPDATE agents SET pool=pool WHERE id=$1"))
        {
            unchanged.Parameters.AddWithValue(id);
            await unchanged.ExecuteNonQueryAsync(token);
        }
        Assert.False(await listener.WaitAsync(300, token));
        Assert.Empty(received);
        await using (var connection = await database.DataSource.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            await using var change = new NpgsqlCommand(
                "UPDATE agents SET pool='committed' WHERE id=$1", connection, transaction);
            change.Parameters.AddWithValue(id);
            await change.ExecuteNonQueryAsync(token);
            change.CommandText = "UPDATE agents SET cpu_percent=25 WHERE id=$1";
            await change.ExecuteNonQueryAsync(token);
            Assert.False(await listener.WaitAsync(300, token));
            await transaction.CommitAsync(token);
        }
        Assert.True(await listener.WaitAsync(5000, token));
        Assert.Equal(["*"], received);
        await using (var delete = database.DataSource.CreateCommand("DELETE FROM agents WHERE id=$1"))
        {
            delete.Parameters.AddWithValue(id);
            await delete.ExecuteNonQueryAsync(token);
        }
        Assert.True(await listener.WaitAsync(5000, token));
        Assert.Equal(["*", "*"], received);
    }
}

public sealed class MachineNotificationsPreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        Migrator.LoadScripts().Where(script => string.CompareOrdinal(script.Name, "0033_machine_notifications.sql") < 0)
            .Select(script => script.Name).ToArray();
}

public sealed class CapacityNotificationsMigrationsTests(CapacityNotificationsPreviousDatabase database)
    : IClassFixture<CapacityNotificationsPreviousDatabase>
{
    [Fact]
    public async Task Capacity_notification_upgrade_preserves_existing_profiles_and_only_notifies_committed_changes()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await CapacityMigrationsTests.SeedAgent(database.DataSource);
        var profile = new CapacityProfile("http", "json", "small");
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO agent_capacities(agent_id,profile_key,capacity_rps,source) VALUES($1,$2,1000,0)
            """))
        {
            seed.Parameters.AddWithValue(id);
            seed.Parameters.AddWithValue(profile.ProfileKey);
            await seed.ExecuteNonQueryAsync(token);
        }
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0034_capacity_notifications.sql"), token);
        await using (var migration = database.DataSource.CreateCommand(sql))
            await migration.ExecuteNonQueryAsync(token);
        await using (var verify = database.DataSource.CreateCommand("SELECT capacity_rps FROM agent_capacities WHERE agent_id=$1 AND profile_key=$2"))
        {
            verify.Parameters.AddWithValue(id);
            verify.Parameters.AddWithValue(profile.ProfileKey);
            Assert.Equal(1000, await verify.ExecuteScalarAsync(token));
        }
        await using var listener = await database.DataSource.OpenConnectionAsync(token);
        var received = new List<string>();
        listener.Notification += (_, notification) => received.Add(notification.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN live_machines", listener))
            await listen.ExecuteNonQueryAsync(token);
        await using (var connection = await database.DataSource.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            await using var change = new NpgsqlCommand("UPDATE agent_capacities SET capacity_rps=1234 WHERE agent_id=$1", connection, transaction);
            change.Parameters.AddWithValue(id);
            await change.ExecuteNonQueryAsync(token);
            await transaction.RollbackAsync(token);
        }
        await using (var unchanged = database.DataSource.CreateCommand("UPDATE agent_capacities SET capacity_rps=capacity_rps WHERE agent_id=$1"))
        {
            unchanged.Parameters.AddWithValue(id);
            await unchanged.ExecuteNonQueryAsync(token);
        }
        Assert.False(await listener.WaitAsync(300, token));
        Assert.Empty(received);
        await using (var connection = await database.DataSource.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            await using var change = new NpgsqlCommand("UPDATE agent_capacities SET capacity_rps=1234 WHERE agent_id=$1", connection, transaction);
            change.Parameters.AddWithValue(id);
            await change.ExecuteNonQueryAsync(token);
            Assert.False(await listener.WaitAsync(300, token));
            await transaction.CommitAsync(token);
        }
        Assert.True(await listener.WaitAsync(5000, token));
        Assert.Equal(["*"], received);
        await using (var insert = database.DataSource.CreateCommand("""
            INSERT INTO agent_capacities(agent_id,profile_key,capacity_rps,source) VALUES($1,$2,3000,0)
            """))
        {
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(new CapacityProfile("http", "json", "large").ProfileKey);
            await insert.ExecuteNonQueryAsync(token);
        }
        Assert.True(await listener.WaitAsync(5000, token));
        await using (var delete = database.DataSource.CreateCommand("DELETE FROM agent_capacities WHERE agent_id=$1"))
        {
            delete.Parameters.AddWithValue(id);
            await delete.ExecuteNonQueryAsync(token);
        }
        Assert.True(await listener.WaitAsync(5000, token));
        Assert.Equal(["*", "*", "*"], received);
    }
}

public sealed class CapacityNotificationsPreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        Migrator.LoadScripts().Where(script => string.CompareOrdinal(script.Name, "0034_capacity_notifications.sql") < 0)
            .Select(script => script.Name).ToArray();
}
