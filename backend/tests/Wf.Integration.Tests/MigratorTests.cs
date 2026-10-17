using System.Diagnostics;
using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.ControlPlane.Data;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests;

public sealed class MigratorTests(MigrationDatabase database) : IClassFixture<MigrationDatabase>
{
    [Fact]
    public async Task Fresh_database_applies_all_embedded_scripts_and_journals_each_exactly_once()
    {
        await using var scope = await Scope();
        var applied = Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance);
        Assert.Equal(Migrator.LoadScripts().Select(script => script.Name), applied);
        Assert.Empty(Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance));
        await using var query = scope.DataSource.CreateCommand("SELECT count(*) FROM schema_versions");
        Assert.Equal((long)applied.Count, await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await using var tables = scope.DataSource.CreateCommand("""
            SELECT count(*) FROM information_schema.tables WHERE table_schema=current_schema()
              AND table_name IN ('runs','run_events','agents','schedules','agent_capacities','commands','leases')
            """);
        Assert.Equal(7L, await tables.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_upgrade_preserves_old_rows_and_empty_partition_families_and_retries_atomically(bool failFirst)
    {
        await using var scope = await Scope();
        var scripts = Migrator.LoadScripts();
        var previous = scripts.Where(script => string.CompareOrdinal(script.Name, "0043") < 0).ToArray();
        Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance, previous);
        var agent = await CapacityMigrationsTests.SeedAgent(scope.DataSource);
        await using var row = scope.DataSource.CreateCommand("SELECT xmin::text FROM agents WHERE id=$1");
        row.Parameters.AddWithValue(agent);
        var before = await row.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await using (var historical = scope.DataSource.CreateCommand("SELECT ensure_run_partitions(date '2020-01-01')"))
            await historical.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var checkpoint = Assert.Single(scripts, script => script.Name == "0043_task_checkpoints.sql");
        if (failFirst)
        {
            var broken = new SqlScript(checkpoint.Name, checkpoint.Contents.Replace("COMMIT;", "SELECT 1/0; COMMIT;", StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() =>
                Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance, [.. previous, broken]));
            await using var absent = scope.DataSource.CreateCommand("SELECT to_regclass('task_checkpoints')::text");
            Assert.Equal(DBNull.Value, await absent.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal([checkpoint.Name], Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance));
        Assert.Empty(Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance));
        Assert.Equal(before, await row.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await using var family = scope.DataSource.CreateCommand("""
            SELECT count(*) FROM pg_inherits i JOIN pg_class p ON p.oid=i.inhparent JOIN pg_class c ON c.oid=i.inhrelid
            WHERE p.relnamespace=current_schema()::regnamespace AND c.relname LIKE '%_202001'
                AND p.relname IN('runs','run_events','run_snapshots','node_runs','task_attempts','reports','artifacts','task_checkpoints')
            """);
        Assert.Equal(8L, await family.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Machine_metadata_upgrade_preserves_existing_rows_and_legacy_writer_columns()
    {
        await using var scope = await Scope();
        var scripts = Migrator.LoadScripts();
        Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance, scripts.Where(script => string.CompareOrdinal(script.Name, "0031") < 0));
        var id = await CapacityMigrationsTests.SeedAgent(scope.DataSource);
        await using var original = scope.DataSource.CreateCommand("SELECT xmin::text FROM agents WHERE id=$1");
        original.Parameters.AddWithValue(id);
        var xmin = await original.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal(scripts.Where(script => string.CompareOrdinal(script.Name, "0031") >= 0).Select(script => script.Name),
            Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance));
        Assert.Equal(xmin, await original.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await using var query = scope.DataSource.CreateCommand(
            "SELECT labels_override IS NULL,pool_override IS NULL,pool,cardinality(labels) FROM agents WHERE id=$1");
        query.Parameters.AddWithValue(id);
        await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("default", reader.GetString(2));
        Assert.Equal(0, reader.GetInt32(3));
        await reader.DisposeAsync();
        Assert.NotEqual(id, await CapacityMigrationsTests.SeedAgent(scope.DataSource));
        Assert.Empty(Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance));
    }

    [Fact]
    public async Task Journaled_previous_release_upgrades_without_rewriting_agents_or_run_deadlines()
    {
        await using var scope = await Scope();
        var scripts = Migrator.LoadScripts();
        Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance,
            scripts.Where(script => string.CompareOrdinal(script.Name, "0012") < 0));
        var agent = await CapacityMigrationsTests.SeedAgent(scope.DataSource);
        var run = Guid.CreateVersion7();
        await using var historical = scope.DataSource.CreateCommand("ALTER TABLE runs ALTER COLUMN deadline_at DROP DEFAULT");
        await historical.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var seed = scope.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,'team/type/legacy',$1,0,'legacy','{}','{}',0,now(),now()+interval '1 hour')
            RETURNING deadline_at
            """);
        seed.Parameters.AddWithValue(run);
        var deadline = await seed.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal(scripts.Where(script => string.CompareOrdinal(script.Name, "0012") >= 0).Select(script => script.Name),
            Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance));
        await using var query = scope.DataSource.CreateCommand("""
            SELECT a.cores,r.deadline_at FROM agents a CROSS JOIN runs r
            WHERE a.id=$1 AND r.run_day=uuid_v7_run_day($2) AND r.id=$2
            """);
        query.Parameters.AddWithValue(agent);
        query.Parameters.AddWithValue(run);
        await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, reader.GetInt32(0));
        Assert.Equal(deadline, reader.GetDateTime(1));
        await reader.DisposeAsync();
        await using var oldWriter = scope.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES(uuid_v7_run_day($1),$1,'team/type/legacy',$1,0,'legacy','{}','{}',0,now())
            RETURNING deadline_at-created_at
            """);
        oldWriter.Parameters.AddWithValue(Guid.CreateVersion7());
        Assert.Equal(TimeSpan.FromHours(24), await oldWriter.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_script_rolls_back_its_schema_data_and_journal_then_retry_applies_once()
    {
        await using var scope = await Scope();
        SqlScript first = new("0001_first.sql", "BEGIN; CREATE TABLE durable(id int PRIMARY KEY); INSERT INTO durable VALUES (1); COMMIT;");
        SqlScript broken = new("0002_second.sql", """
            BEGIN; CREATE TABLE transient(id int); INSERT INTO durable VALUES (2); SELECT 1/0; COMMIT;
            """);
        var error = Assert.Throws<InvalidOperationException>(() =>
            Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance, [first, broken]));
        Assert.Contains("0002_second.sql", error.Message);
        await using var state = scope.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM durable),(SELECT count(*) FROM schema_versions),to_regclass('transient')::text
            """);
        await using var reader = await state.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.True(reader.IsDBNull(2));
        await reader.DisposeAsync();
        SqlScript fixedScript = new("0002_second.sql", "CREATE TABLE transient(id int); INSERT INTO durable VALUES (2);");
        Assert.Equal(["0002_second.sql"],
            Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance, [first, fixedScript]));
        Assert.Empty(Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance, [first, fixedScript]));
    }

    [Fact]
    public async Task Concurrent_deployers_serialize_before_reading_the_journal()
    {
        await using var scope = await Scope();
        var scripts = new[] { new SqlScript("0001_concurrent.sql", "CREATE TABLE concurrent(id int); SELECT pg_sleep(0.1);") };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            Migrator.Upgrade(scope.ConnectionString, NullLogger.Instance, scripts), TestContext.Current.CancellationToken)));
        Assert.Equal([0, 1], results.Select(result => result.Count).Order());
    }

    [Theory]
    [InlineData("0001_contract.sql", "DROP TABLE runs;")]
    [InlineData("0001_contract.sql", "ALTER TABLE runs ADD COLUMN secret text NOT NULL;")]
    [InlineData("0001_transaction.sql", "CREATE TABLE before_commit(id int); COMMIT; CREATE TABLE after_commit(id int);")]
    [InlineData("../escape.sql", "SELECT 1;")]
    public void Invalid_release_fails_before_opening_a_database(string name, string sql)
    {
        var error = Record.Exception(() => Migrator.Upgrade("not a connection string", NullLogger.Instance, [new(name, sql)]));
        Assert.True(error is InvalidDataException or ArgumentException);
        Assert.DoesNotContain("initialization string", error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Actual_control_plane_migrate_mode_exits_successfully_without_starting_the_server()
    {
        await using var scope = await Scope();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Migrator).Assembly.Location);
        start.ArgumentList.Add("--migrate");
        start.Environment["ConnectionStrings__Workflow"] = scope.ConnectionString;
        start.Environment["Engine__Port"] = "59999";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60),
                TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
            await using var count = scope.DataSource.CreateCommand("SELECT count(*) FROM schema_versions");
            Assert.Equal((long)Migrator.LoadScripts().Count, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private async Task<MigrationScope> Scope()
    {
        var schema = $"migration_{Guid.NewGuid():N}";
        await using var create = database.DataSource.CreateCommand($"CREATE SCHEMA \"{schema}\"");
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new(new NpgsqlConnectionStringBuilder(database.ConnectionString) { SearchPath = schema }.ConnectionString, schema);
    }

    private sealed class MigrationScope(string connectionString, string schema) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public NpgsqlDataSource DataSource { get; } = NpgsqlDataSource.Create(connectionString);

        public async ValueTask DisposeAsync()
        {
            await using (var drop = DataSource.CreateCommand($"DROP SCHEMA \"{schema}\" CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await DataSource.DisposeAsync();
        }
    }
}

public sealed class MigrationDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => [];
    public string ConnectionString => DatabaseConnectionString;
}
