using Microsoft.AspNetCore.Authorization;
using Wf.Contracts.Registry;

namespace Wf.ControlPlane.Auth;

public sealed record CallerPermissions(string Scope, string Id, bool Viewer, bool Author, bool Operator, bool Admin);

public static class PermissionsEndpoints
{
    public static IEndpointRouteBuilder MapPermissionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/permissions/{scope}", Read).RequireAuthorization()
            .Produces<CallerPermissions>().ProducesProblem(400).ProducesProblem(403);
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string scope, string id,
        IAuthorizationService authorization)
    {
        context.Response.Headers.CacheControl = "no-store";
        TeamResourceKind? kind = scope switch
        {
            "team" => TeamResourceKind.Team,
            "type" => TeamResourceKind.Type,
            "run" => TeamResourceKind.Run,
            "series" => TeamResourceKind.Series,
            "machines" => TeamResourceKind.Machines,
            _ => null
        };
        if (kind is null || string.IsNullOrWhiteSpace(id) || id.Length > 1024 || id.Any(char.IsControl))
            return Results.Problem(statusCode: 400, title: "Invalid permission resource",
                detail: "A supported scope and resource ID of 1-1024 characters without control characters are required.");
        var resource = new TeamResource(kind.Value, id, context.RequestAborted);
        if (!await Allowed(RegistryRole.Viewer))
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        return Results.Ok(new CallerPermissions(scope, id, true, await Allowed(RegistryRole.Author),
            await Allowed(RegistryRole.Operator), await Allowed(RegistryRole.Admin)));

        async Task<bool> Allowed(RegistryRole role) =>
            (await authorization.AuthorizeAsync(context.User, resource, TeamAuthorizationExtensions.Policy(role))).Succeeded;
    }
}
