using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Auth;

public sealed class RoleBindingTests(RoleBindingDatabase database) : IClassFixture<RoleBindingDatabase>
{
    [Fact]
    public async Task Registration_maps_all_roles_in_exact_team_and_type_scopes()
    {
        var team = Team();
        var teamGroup = Guid.NewGuid();
        var typeGroup = Guid.NewGuid();
        var input = Registry(team) with
        {
            RoleBindings = [.. Enum.GetValues<RegistryRole>().Select(role => new RegistryRoleBinding(role, teamGroup))],
            Types = [new(team, "release", "description", new([]),
                [new(RegistryRole.Author, typeGroup), new(RegistryRole.Viewer, teamGroup)])]
        };
        Assert.True(await Save(input));
        foreach (var role in Enum.GetValues<RegistryRole>())
            Assert.True(await Has(RoleScopeKind.Team, team, role, teamGroup));
        Assert.True(await Has(RoleScopeKind.Type, team + "/release", RegistryRole.Author, typeGroup));
        Assert.True(await Has(RoleScopeKind.Type, team + "/release", RegistryRole.Viewer, teamGroup));
        Assert.False(await Has(RoleScopeKind.Global, "*", RegistryRole.Admin, teamGroup));
        Assert.False(await Has(RoleScopeKind.Type, team + "/other", RegistryRole.Author, typeGroup));
    }

    [Fact]
    public async Task Removed_group_and_removed_type_bindings_disappear_at_next_commit()
    {
        var team = Team();
        var group = Guid.NewGuid();
        var original = Registry(team) with
        {
            RoleBindings = [new(RegistryRole.Operator, group)],
            Types = [new(team, "release", "description", new([]), [new(RegistryRole.Author, group)])]
        };
        await Save(original);
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await new RegistryWriter().UpsertAsync(connection, transaction, new(team, []),
            TestContext.Current.CancellationToken);
        Assert.True(await Has(RoleScopeKind.Team, team, RegistryRole.Operator, group));
        Assert.True(await Has(RoleScopeKind.Type, team + "/release", RegistryRole.Author, group));
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        Assert.False(await Has(RoleScopeKind.Team, team, RegistryRole.Operator, group));
        Assert.False(await Has(RoleScopeKind.Type, team + "/release", RegistryRole.Author, group));
    }

    [Fact]
    public async Task Registry_sync_preserves_global_other_team_and_explicit_user_bindings()
    {
        var team = Team();
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();
        await Seed(RoleScopeKind.Global, "*", RegistryRole.Admin, group, RolePrincipalKind.Group);
        await Seed(RoleScopeKind.Team, team + "-other", RegistryRole.Viewer, group, RolePrincipalKind.Group);
        await Seed(RoleScopeKind.Type, team + "-other/release", RegistryRole.Author, group, RolePrincipalKind.Group);
        await Seed(RoleScopeKind.Team, team, RegistryRole.Operator, user, RolePrincipalKind.User);
        await Seed(RoleScopeKind.Type, team + "/removed", RegistryRole.Viewer, user, RolePrincipalKind.User);
        await Save(new(team, []));
        Assert.True(await Has(RoleScopeKind.Global, "*", RegistryRole.Admin, group));
        Assert.True(await Has(RoleScopeKind.Team, team + "-other", RegistryRole.Viewer, group));
        Assert.True(await Has(RoleScopeKind.Type, team + "-other/release", RegistryRole.Author, group));
        Assert.True(await Has(RoleScopeKind.Team, team, RegistryRole.Operator, user));
        Assert.True(await Has(RoleScopeKind.Type, team + "/removed", RegistryRole.Viewer, user));
    }

    [Fact]
    public async Task Role_change_replaces_only_the_changed_binding_and_is_idempotent()
    {
        var team = Team();
        var group = Guid.NewGuid();
        var input = Registry(team) with { RoleBindings = [new(RegistryRole.Viewer, group)] };
        await Save(input);
        var before = await Identity(team);
        Assert.False(await Save(input));
        Assert.Equal(before, await Identity(team));
        Assert.True(await Save(input with { RoleBindings = [new(RegistryRole.Author, group)] }));
        Assert.False(await Has(RoleScopeKind.Team, team, RegistryRole.Viewer, group));
        Assert.True(await Has(RoleScopeKind.Team, team, RegistryRole.Author, group));
    }

    [Fact]
    public async Task Existing_unchanged_snapshot_reconciles_missing_bindings_without_rewriting_team()
    {
        var team = Team();
        var group = Guid.NewGuid();
        var input = Registry(team) with { RoleBindings = [new(RegistryRole.Viewer, group)] };
        await Save(input);
        await using var state = database.DataSource.CreateCommand("SELECT xmin::text FROM teams WHERE id=$1");
        state.Parameters.AddWithValue(team);
        var before = await state.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await using var remove = database.DataSource.CreateCommand("DELETE FROM role_bindings WHERE scope_id=$1");
        remove.Parameters.AddWithValue(team);
        Assert.Equal(1, await remove.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.True(await Save(input));
        Assert.Equal(before, await state.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.True(await Has(RoleScopeKind.Team, team, RegistryRole.Viewer, group));
    }

    [Fact]
    public async Task Rollback_preserves_previous_permissions_and_registry_snapshot_together()
    {
        var team = Team();
        var old = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var input = Registry(team) with { RoleBindings = [new(RegistryRole.Author, old)] };
        await Save(input);
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            input with { RoleBindings = [new(RegistryRole.Admin, replacement)] }, TestContext.Current.CancellationToken);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.True(await Has(RoleScopeKind.Team, team, RegistryRole.Author, old));
        Assert.False(await Has(RoleScopeKind.Team, team, RegistryRole.Admin, replacement));
    }

    [Fact]
    public async Task Concurrent_registrations_leave_only_bindings_from_the_final_snapshot()
    {
        var team = Team();
        var groups = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(groups.Select(group =>
            Save(Registry(team) with { RoleBindings = [new(RegistryRole.Viewer, group)] })));
        await using var read = database.DataSource.CreateCommand("""
            SELECT (registry->'roleBindings'->0->>'groupId')::uuid,
                (SELECT principal_id FROM role_bindings WHERE scope_kind=1 AND scope_id=$1)
            FROM teams WHERE id=$1
            """);
        read.Parameters.AddWithValue(team);
        await using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(reader.GetGuid(0), reader.GetGuid(1));
    }

    [Theory]
    [InlineData(-1, "team", 0, 0)]
    [InlineData(3, "team", 0, 0)]
    [InlineData(0, "not-global", 0, 0)]
    [InlineData(1, "team/type", 0, 0)]
    [InlineData(2, "team", 0, 0)]
    [InlineData(2, "team/type/extra", 0, 0)]
    [InlineData(1, "team", -1, 0)]
    [InlineData(1, "team", 4, 0)]
    [InlineData(1, "team", 0, -1)]
    [InlineData(1, "team", 0, 2)]
    public async Task Database_rejects_unknown_scope_role_or_principal_kind(int kind, string scope, int role, int principalKind)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            Seed((RoleScopeKind)kind, scope, (RegistryRole)role, Guid.NewGuid(), (RolePrincipalKind)principalKind));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Empty_principal_duplicate_binding_and_non_v7_row_id_are_rejected()
    {
        var team = Team();
        var group = Guid.NewGuid();
        var empty = await Assert.ThrowsAsync<PostgresException>(() =>
            Seed(RoleScopeKind.Team, team, RegistryRole.Viewer, Guid.Empty, RolePrincipalKind.Group));
        Assert.Equal(PostgresErrorCodes.CheckViolation, empty.SqlState);
        await Seed(RoleScopeKind.Team, team, RegistryRole.Viewer, group, RolePrincipalKind.Group);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            Seed(RoleScopeKind.Team, team, RegistryRole.Viewer, group, RolePrincipalKind.Group));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        await using var invalidId = database.DataSource.CreateCommand("""
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind) VALUES($1,0,'*',3,$2,0)
            """);
        invalidId.Parameters.AddWithValue(Guid.NewGuid());
        invalidId.Parameters.AddWithValue(Guid.NewGuid());
        var id = await Assert.ThrowsAsync<PostgresException>(() =>
            invalidId.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, id.SqlState);
    }

    private async Task<bool> Save(TeamRegistry registry)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var changed = await new RegistryWriter().UpsertAsync(connection, transaction, registry,
            TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return changed;
    }

    private async Task Seed(RoleScopeKind kind, string scope, RegistryRole role, Guid principal, RolePrincipalKind principalKind)
    {
        await using var insert = database.DataSource.CreateCommand("""
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind) VALUES($1,$2,$3,$4,$5,$6)
            """);
        insert.Parameters.AddWithValue(Guid.CreateVersion7());
        insert.Parameters.AddWithValue((short)kind);
        insert.Parameters.AddWithValue(scope);
        insert.Parameters.AddWithValue((short)role);
        insert.Parameters.AddWithValue(principal);
        insert.Parameters.AddWithValue((short)principalKind);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<bool> Has(RoleScopeKind kind, string scope, RegistryRole role, Guid principal)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM role_bindings WHERE scope_kind=$1 AND scope_id=$2 AND role=$3 AND principal_id=$4)
            """);
        query.Parameters.AddWithValue((short)kind);
        query.Parameters.AddWithValue(scope);
        query.Parameters.AddWithValue((short)role);
        query.Parameters.AddWithValue(principal);
        return (bool)(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<string> Identity(string team)
    {
        await using var query = database.DataSource.CreateCommand(
            "SELECT id::text||':'||xmin::text FROM role_bindings WHERE scope_kind=1 AND scope_id=$1");
        query.Parameters.AddWithValue(team);
        return (string)(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static string Team() => "team-" + Guid.NewGuid().ToString("N");
    private static TeamRegistry Registry(string team) => new(team, [new(team, "release", "description", new([]))]);
}

public sealed class RoleBindingDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => ["0015_team_type_registry.sql", "0016_role_bindings.sql"];
}
