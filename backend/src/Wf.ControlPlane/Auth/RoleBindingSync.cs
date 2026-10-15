using Npgsql;
using Wf.Contracts.Registry;

namespace Wf.ControlPlane.Auth;

public enum RoleScopeKind { Global = 0, Team = 1, Type = 2 }
public enum RolePrincipalKind { Group = 0, User = 1 }

public static class RoleBindingSync
{
    // RegistryWriter validates and locks the team before calling; sync participates in its caller-owned transaction.
    internal static async Task<bool> SyncAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        TeamRegistry registry, CancellationToken cancellationToken)
    {
        var bindings = new List<(RoleScopeKind Kind, string Scope, RegistryRole Role, Guid Principal)>();
        foreach (var binding in registry.RoleBindings)
            bindings.Add((RoleScopeKind.Team, registry.Team, binding.Role, binding.GroupId));
        foreach (var type in registry.Types)
            foreach (var binding in type.RoleBindings)
                bindings.Add((RoleScopeKind.Type, registry.Team + "/" + type.Name, binding.Role, binding.GroupId));

        await using var sync = new NpgsqlCommand("""
            WITH desired AS (
                SELECT * FROM unnest($2::uuid[],$3::smallint[],$4::text[],$5::smallint[],$6::uuid[])
                    AS v(id,scope_kind,scope_id,role,principal_id)
            ), removed AS (
                DELETE FROM role_bindings b
                WHERE b.principal_kind=0
                  AND ((b.scope_kind=1 AND b.scope_id=$1)
                    OR (b.scope_kind=2 AND starts_with(b.scope_id,$1||'/')))
                  AND NOT EXISTS (
                    SELECT 1 FROM desired d WHERE d.scope_kind=b.scope_kind AND d.scope_id=b.scope_id
                      AND d.role=b.role AND d.principal_id=b.principal_id)
                RETURNING 1
            ), inserted AS (
                INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
                SELECT id,scope_kind,scope_id,role,principal_id,0 FROM desired
                ON CONFLICT(scope_kind,scope_id,role,principal_id) DO NOTHING
                RETURNING 1
            )
            SELECT (SELECT count(*) FROM removed)+(SELECT count(*) FROM inserted)
            """, connection, transaction);
        sync.Parameters.AddWithValue(registry.Team);
        sync.Parameters.AddWithValue(bindings.Select(_ => Guid.CreateVersion7()).ToArray());
        sync.Parameters.AddWithValue(bindings.Select(binding => (short)binding.Kind).ToArray());
        sync.Parameters.AddWithValue(bindings.Select(binding => binding.Scope).ToArray());
        sync.Parameters.AddWithValue(bindings.Select(binding => (short)binding.Role).ToArray());
        sync.Parameters.AddWithValue(bindings.Select(binding => binding.Principal).ToArray());
        return (long)(await sync.ExecuteScalarAsync(cancellationToken))! > 0;
    }
}
