using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Agents;
using Wf.Validation.Rules;

namespace Wf.Integration.Tests.Registry;

public sealed class RegistryPersistenceTests(RegistryDatabase database) : IClassFixture<RegistryDatabase>
{
    [Fact]
    public async Task Http_read_observes_complete_snapshot_only_after_registration_transaction_commits()
    {
        var team = $"team-{Guid.NewGuid():N}";
        var original = Registry(team, "release");
        await Save(original);
        await using var server = await StartServer();
        var old = Assert.Single((await server.Client.GetFromJsonAsync<TeamRegistry[]>("/api/v1/teams",
            TestContext.Current.CancellationToken))!, registry => registry.Team == team);
        Assert.Equal("release", Assert.Single(old.Types).Name);

        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var replacement = Registry(team, "smoke") with
        {
            Presentation = new(TypeOrder: ["smoke"]),
            RoleBindings = [new(RegistryRole.Viewer, Guid.NewGuid())]
        };
        Assert.True(await new RegistryWriter().UpsertAsync(connection, transaction, replacement,
            TestContext.Current.CancellationToken));
        var during = Assert.Single((await server.Client.GetFromJsonAsync<TeamRegistry[]>("/api/v1/teams",
            TestContext.Current.CancellationToken))!, registry => registry.Team == team);
        Assert.Equal("release", Assert.Single(during.Types).Name);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        var after = Assert.Single((await server.Client.GetFromJsonAsync<TeamRegistry[]>("/api/v1/teams",
            TestContext.Current.CancellationToken))!, registry => registry.Team == team);
        Assert.Equal("smoke", Assert.Single(after.Types).Name);
        Assert.Equal("smoke", Assert.Single(after.Presentation!.TypeOrder));
        Assert.Equal(replacement.RoleBindings, after.RoleBindings);
        Assert.Equal(["smoke"], await Names(team));
        var endpoint = Assert.Single(((IEndpointRouteBuilder)server.App).DataSources.SelectMany(source => source.Endpoints));
        Assert.Equal(AuthExtensions.Admin, Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
    }

    [Fact]
    public async Task Rollback_of_later_registration_work_preserves_old_team_and_types()
    {
        var team = $"team-{Guid.NewGuid():N}";
        await Save(Registry(team, "release"));
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await new RegistryWriter().UpsertAsync(connection, transaction, Registry(team, "smoke"),
            TestContext.Current.CancellationToken);
        await using var fail = new NpgsqlCommand("SELECT 1/0", connection, transaction);
        await Assert.ThrowsAsync<PostgresException>(() => fail.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["release"], await Names(team));
        await using var query = database.DataSource.CreateCommand("SELECT registry->'types'->0->>'name' FROM teams WHERE id=$1");
        query.Parameters.AddWithValue(team);
        Assert.Equal("release", await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Same_snapshot_is_idempotent_without_touching_rows()
    {
        var registry = Registry($"team-{Guid.NewGuid():N}", "release");
        Assert.True(await Save(registry));
        var before = await Xmin(registry.Team);
        Assert.False(await Save(registry));
        Assert.Equal(before, await Xmin(registry.Team));
    }

    [Fact]
    public async Task Replacement_can_rename_a_type_to_same_normalized_identity()
    {
        var team = $"team-{Guid.NewGuid():N}";
        await Save(Registry(team, "Load-Test"));
        await Save(Registry(team, "load_tests"));
        Assert.Equal(["load_tests"], await Names(team));
        await using var query = database.DataSource.CreateCommand(
            "SELECT id,normalized_name FROM workflow_types WHERE team_id=$1");
        query.Parameters.AddWithValue(team);
        await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(team + "/load_tests", reader.GetString(0));
        Assert.Equal("loadtest", reader.GetString(1));
    }

    [Fact]
    public async Task Empty_snapshot_removes_all_types_without_deleting_team()
    {
        var team = $"team-{Guid.NewGuid():N}";
        await Save(Registry(team, "release", "smoke"));
        Assert.True(await Save(Registry(team)));
        Assert.Empty(await Names(team));
    }

    [Fact]
    public async Task Same_type_names_are_allowed_in_distinct_teams()
    {
        var first = $"team-{Guid.NewGuid():N}";
        var second = $"team-{Guid.NewGuid():N}";
        await Save(Registry(first, "release"));
        await Save(Registry(second, "release"));
        Assert.Equal(["release"], await Names(first));
        Assert.Equal(["release"], await Names(second));
    }

    [Fact]
    public async Task Concurrent_full_snapshots_never_mix_their_type_sets()
    {
        var team = $"team-{Guid.NewGuid():N}";
        var inputs = new[] { Registry(team, "release"), Registry(team, "smoke"), Registry(team, "integration") };
        Assert.All(await Task.WhenAll(inputs.Select(input => Save(input))), Assert.True);
        await using var query = database.DataSource.CreateCommand("SELECT registry::text FROM teams WHERE id=$1");
        query.Parameters.AddWithValue(team);
        var stored = JsonSerializer.Deserialize(Assert.IsType<string>(
            await query.ExecuteScalarAsync(TestContext.Current.CancellationToken)), TeamRegistryJsonContext.Default.TeamRegistry)!;
        Assert.Equal(stored.Types.Select(type => type.Name).Order(StringComparer.Ordinal), await Names(team));
    }

    [Fact]
    public async Task Shared_validation_and_trusted_quota_reject_before_any_snapshot_is_written()
    {
        var team = $"team-{Guid.NewGuid():N}";
        await Save(Registry(team, "release"));
        var duplicate = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Save(Registry(team, "Load-Test", "load_tests")));
        Assert.Contains(duplicate.Errors, error => error.RuleId == "WFV016");
        var quota = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Save(Registry(team, "release", "smoke", "integration"), new RegistryWriter(2)));
        Assert.Contains(quota.Errors, error => error.RuleId == "WFV017" && error.Hint.Contains("2"));
        Assert.Equal(["release"], await Names(team));
    }

    [Fact]
    public async Task Database_normalized_unique_key_and_foreign_keys_back_the_writer()
    {
        var team = $"team-{Guid.NewGuid():N}";
        await Save(Registry(team, "Load-Test"));
        await using var duplicate = database.DataSource.CreateCommand("""
            INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
            VALUES($1||'/other',$1,'other','loadtest','description','{"keys":[]}')
            """);
        duplicate.Parameters.AddWithValue(team);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            duplicate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        duplicate.Parameters[0].Value = "unknown-" + Guid.NewGuid().ToString("N");
        error = await Assert.ThrowsAsync<PostgresException>(() =>
            duplicate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Theory]
    [InlineData("team/id")]
    [InlineData("team\\id")]
    [InlineData("team\nid")]
    public async Task Invalid_persisted_identifier_is_rejected_by_database_too(string team)
    {
        await using var insert = database.DataSource.CreateCommand("INSERT INTO teams(id,name) VALUES($1,'name')");
        insert.Parameters.AddWithValue(team);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Notification_is_delivered_only_after_commit_and_not_for_idempotent_snapshot()
    {
        var registry = Registry($"team-{Guid.NewGuid():N}", "release");
        await using var listener = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var listen = new NpgsqlCommand("LISTEN registry", listener))
            await listen.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, notification) =>
        {
            if (notification.Payload == registry.Team) received.TrySetResult(notification.Payload);
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var waiting = WaitForNotification();
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await new RegistryWriter().UpsertAsync(connection, transaction, registry, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(received.Task.IsCompleted);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        await waiting;
        Assert.Equal(registry.Team, await received.Task);
        // The no-op snapshot also leaves xmin untouched (separate test); it must report no change.
        Assert.False(await Save(registry));

        async Task WaitForNotification()
        {
            while (!received.Task.IsCompleted) await listener.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task Foreign_transaction_and_canceled_input_fail_explicitly()
    {
        var registry = Registry($"team-{Guid.NewGuid():N}", "release");
        await using var first = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var second = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await first.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => new RegistryWriter().UpsertAsync(second, transaction, registry,
            TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RegistryWriter().UpsertAsync(first, transaction, registry, canceled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegistryWriter(0));
    }

    private async Task<bool> Save(TeamRegistry registry, RegistryWriter? writer = null)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var changed = await (writer ?? new RegistryWriter()).UpsertAsync(connection, transaction, registry,
            TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return changed;
    }

    private async Task<string[]> Names(string team)
    {
        var result = new List<string>();
        await using var query = database.DataSource.CreateCommand(
            "SELECT name FROM workflow_types WHERE team_id=$1 ORDER BY name COLLATE \"C\"");
        query.Parameters.AddWithValue(team);
        await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    private async Task<string> Xmin(string team)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT t.xmin::text||':'||w.xmin::text FROM teams t JOIN workflow_types w ON w.team_id=t.id WHERE t.id=$1
            """);
        query.Parameters.AddWithValue(team);
        return Assert.IsType<string>(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static TeamRegistry Registry(string team, params string[] names) =>
        new(team, [.. names.Select(name => new WorkflowTypeDefinition(team, name, "description", new([])))]);

    private async Task<RegistryServer> StartServer()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Authentication:Mode"] = "Development";
        builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(database.DataSource);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRegistryEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new(app, new HttpClient
        {
            BaseAddress = new(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single())
        });
    }

    private sealed record RegistryServer(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}

public sealed class RegistryUpgradeTests(RegistryPreviousDatabase database) : IClassFixture<RegistryPreviousDatabase>
{
    [Fact]
    public async Task Upgrade_preserves_schedule_and_claimed_fire()
    {
        var id = Guid.CreateVersion7();
        await using var seed = database.DataSource.CreateCommand("""
            WITH inserted AS (
                INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
                VALUES($1,'team/type/old',1,'old','Etc/UTC',60,'old','old') RETURNING id
            )
            INSERT INTO schedule_fires(schedule_id,fire_at,outcome) SELECT id,now(),2 FROM inserted
            """);
        seed.Parameters.AddWithValue(id);
        await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "0015_team_type_registry.sql"),
            TestContext.Current.CancellationToken);
        await using var migrate = database.DataSource.CreateCommand(sql);
        await migrate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var verify = database.DataSource.CreateCommand("""
            SELECT s.name,f.outcome FROM schedules s JOIN schedule_fires f ON f.schedule_id=s.id WHERE s.id=$1
            """);
        verify.Parameters.AddWithValue(id);
        await using var reader = await verify.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("old", reader.GetString(0));
        Assert.Equal((short)2, reader.GetInt16(1));
    }
}

public sealed class RegistryDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => ["0015_team_type_registry.sql", "0016_role_bindings.sql"];
}

public sealed class RegistryPreviousDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts()
        .Where(script => string.CompareOrdinal(script.Name, "0015") < 0).Select(script => script.Name).ToArray();
}
