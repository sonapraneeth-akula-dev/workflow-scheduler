using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Wf.Contracts.Registry;

namespace Wf.ControlPlane.Auth;

public enum TeamResourceKind { Team, Type, Run, Series, Machines }
public sealed record TeamResource(TeamResourceKind Kind, string Id, CancellationToken CancellationToken = default);
public sealed record TeamRoleRequirement(RegistryRole Role) : IAuthorizationRequirement;
public sealed record TeamResourceRoute(TeamResourceKind Kind, string Parameter);

public sealed class TeamRoleHandler(NpgsqlDataSource dataSource) : AuthorizationHandler<TeamRoleRequirement>
{
    public const string DenialReason = "The caller does not have the required role for this resource.";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TeamRoleRequirement requirement)
    {
        var resource = context.Resource as TeamResource;
        var token = resource?.CancellationToken ?? CancellationToken.None;
        if (context.Resource is HttpContext http)
        {
            token = http.RequestAborted;
            var route = http.GetEndpoint()?.Metadata.GetMetadata<TeamResourceRoute>();
            if (route is not null && http.Request.RouteValues[route.Parameter] is string id)
                resource = new(route.Kind, id);
        }
        if (context.User.Identity?.IsAuthenticated != true || resource is null ||
            !Enum.IsDefined(requirement.Role) || !Enum.IsDefined(resource.Kind) ||
            string.IsNullOrWhiteSpace(resource.Id) || resource.Id.Length > 1024 ||
            resource.Id.Any(char.IsControl))
        {
            Deny();
            return;
        }

        var globalAdmin = context.User.IsInRole(AuthExtensions.Admin);
        if (resource.Kind == TeamResourceKind.Machines)
        {
            if (globalAdmin && resource.Id == "*") context.Succeed(requirement);
            else Deny();
            return;
        }
        var scope = await Resolve(resource, token);
        if (scope is null)
        {
            Deny();
            return;
        }
        if (globalAdmin)
        {
            context.Succeed(requirement);
            return;
        }
        var user = context.User;
        if (user.HasClaim("idtyp", "app") || user.IsInRole(AuthExtensions.Agent) ||
            user.IsInRole(AuthExtensions.Registrar) ||
            !(user.HasClaim("idtyp", "user") || user.HasClaim(claim => claim.Type == "scp" && !string.IsNullOrWhiteSpace(claim.Value))) ||
            !Guid.TryParse(user.FindFirstValue("oid"), out var userId) || userId == Guid.Empty ||
            user.HasClaim(claim => claim.Type is "hasgroups" or "_claim_names"))
        {
            Deny();
            return;
        }
        var groups = new HashSet<Guid>();
        foreach (var claim in user.FindAll("groups"))
        {
            if (!Guid.TryParse(claim.Value, out var group) || group == Guid.Empty)
            {
                Deny();
                return;
            }
            groups.Add(group);
        }
        await using var query = dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM role_bindings
                WHERE ((scope_kind=1 AND scope_id=$1) OR (scope_kind=2 AND scope_id=$2))
                  AND ((principal_kind=1 AND principal_id=$3) OR (principal_kind=0 AND principal_id=ANY($4)))
                  AND (role=3 OR role=$5 OR ($5=0 AND role IN (1,2))))
            """);
        query.Parameters.AddWithValue(scope.Value.Team);
        query.Parameters.AddWithValue(scope.Value.Type ?? "");
        query.Parameters.AddWithValue(userId);
        query.Parameters.AddWithValue(groups.ToArray());
        query.Parameters.AddWithValue((short)requirement.Role);
        if (await query.ExecuteScalarAsync(token) is true) context.Succeed(requirement);
        else Deny();
        return;

        void Deny() => context.Fail(new AuthorizationFailureReason(this, DenialReason));
    }

    private async Task<(string Team, string? Type)?> Resolve(TeamResource resource, CancellationToken token)
    {
        var sql = resource.Kind switch
        {
            TeamResourceKind.Team => "SELECT id,NULL::text FROM teams WHERE id=$1",
            TeamResourceKind.Type => "SELECT team_id,id FROM workflow_types WHERE id=$1",
            TeamResourceKind.Run => """
                SELECT t.team_id,t.id FROM runs r JOIN workflow_types t
                  ON t.id=split_part(r.workflow_id,'/',1)||'/'||split_part(r.workflow_id,'/',2)
                WHERE r.id=$1 AND r.run_day=uuid_v7_run_day($1)
                  AND r.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'
                """,
            TeamResourceKind.Series => """
                WITH owners AS (
                    SELECT workflow_id FROM run_series WHERE id=$1
                    UNION SELECT workflow_id FROM runs WHERE series_id=$1)
                SELECT DISTINCT t.team_id,t.id FROM owners o LEFT JOIN workflow_types t
                  ON t.id=split_part(o.workflow_id,'/',1)||'/'||split_part(o.workflow_id,'/',2)
                    AND o.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'
                LIMIT 2
                """,
            _ => throw new InvalidOperationException("Unsupported resource kind.")
        };
        await using var query = dataSource.CreateCommand(sql);
        if (resource.Kind is TeamResourceKind.Run or TeamResourceKind.Series)
        {
            if (!Guid.TryParseExact(resource.Id, "D", out var id) || id == Guid.Empty) return null;
            query.Parameters.AddWithValue(id);
        }
        else query.Parameters.AddWithValue(resource.Id);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0)) return null;
        var result = (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        return await reader.ReadAsync(token) ? null : result;
    }
}

public static class TeamAuthorizationExtensions
{
    public static IServiceCollection AddTeamAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            foreach (var role in Enum.GetValues<RegistryRole>())
                options.AddPolicy(Policy(role), policy =>
                    policy.RequireAuthenticatedUser().AddRequirements(new TeamRoleRequirement(role)));
        });
        services.AddScoped<IAuthorizationHandler, TeamRoleHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, TeamAuthorizationResultHandler>();
        return services;
    }

    public static TBuilder RequireTeamRole<TBuilder>(this TBuilder builder, RegistryRole role,
        TeamResourceKind kind, string routeParameter) where TBuilder : IEndpointConventionBuilder
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(routeParameter);
        builder.Add(endpoint => endpoint.Metadata.Add(new TeamResourceRoute(kind, routeParameter)));
        return builder.RequireAuthorization(Policy(role));
    }

    public static string Policy(RegistryRole role) => "Team." + role;

    public static ProblemDetails Forbidden() => new()
    {
        Status = StatusCodes.Status403Forbidden,
        Title = "Forbidden",
        Detail = TeamRoleHandler.DenialReason,
        Extensions = { ["reason"] = "insufficient_scope_role" }
    };

    private sealed class TeamAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler fallback = new();

        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
            PolicyAuthorizationResult result)
        {
            if (result.Forbidden && policy.Requirements.OfType<TeamRoleRequirement>().Any())
            {
                await Results.Problem(Forbidden()).ExecuteAsync(context);
                return;
            }
            await fallback.HandleAsync(next, context, policy, result);
        }
    }
}
