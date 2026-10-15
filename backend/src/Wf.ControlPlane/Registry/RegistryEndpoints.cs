using System.Text.Json;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Registry;

public static class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Bootstrap reads remain global-Admin-only until the scoped catalogue handler is available.
        endpoints.MapGet("/api/v1/teams", ReadTeams).RequireAuthorization(AuthExtensions.Admin);
        return endpoints;
    }

    private static async Task<IResult> ReadTeams(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var registries = new List<TeamRegistry>();
        await using var query = dataSource.CreateCommand("SELECT registry::text FROM teams ORDER BY id COLLATE \"C\"");
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            registries.Add(JsonSerializer.Deserialize(reader.GetString(0), TeamRegistryJsonContext.Default.TeamRegistry)
                ?? throw new InvalidDataException("A persisted team registry is null."));
        return Results.Ok(registries);
    }
}
