using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Registry;

public static class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/teams", CatalogEndpoints.ReadTeams).RequireAuthorization()
            .Produces<CatalogTeam[]>();
        return endpoints;
    }
}
