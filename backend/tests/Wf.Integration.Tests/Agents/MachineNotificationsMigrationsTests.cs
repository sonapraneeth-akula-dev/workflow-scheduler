using Npgsql;
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
