using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/teams/{team}/types", ReadTypes).RequireAuthorization()
            .Produces<CatalogType[]>().ProducesProblem(400).ProducesProblem(404);
        endpoints.MapGet("/api/v1/workflows", ReadWorkflows).RequireAuthorization()
            .Produces<CatalogWorkflowPage>().ProducesProblem(400)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "team", "type", "cursor", "limit" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query,
                        Schema = new OpenApiSchema { Type = name == "limit" ? JsonSchemaType.Integer : JsonSchemaType.String }
                    });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    public static async Task<IResult> ReadTeams(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization) =>
        Results.Ok((await VisibleTeams(context, source, authorization)).Select(CatalogTeam.From).ToArray());

    private static async Task<IResult> ReadTypes(HttpContext context, string team, NpgsqlDataSource source,
        IAuthorizationService authorization)
    {
        if (!CatalogQuery.ValidSegment(team))
            return Results.Problem(statusCode: 400, title: "Invalid team", detail: "Use the exact registered team name.");
        var visible = (await VisibleTeams(context, source, authorization, team)).SingleOrDefault();
        if (visible is null) return Results.Problem(statusCode: 404, title: "Team not found");
        var result = new List<CatalogType>();
        foreach (var type in visible.Types)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            await using var query = source.CreateCommand("""
                SELECT latest.status,count(*) FROM workflows w
                LEFT JOIN LATERAL (
                    SELECT r.status FROM runs r WHERE r.workflow_id=w.id ORDER BY r.created_at DESC,r.id DESC LIMIT 1
                ) latest ON true
                WHERE w.type_id=$1 AND w.deleted_at IS NULL
                GROUP BY latest.status
                """);
            query.Parameters.AddWithValue(team + "/" + type.Name);
            await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
            while (await reader.ReadAsync(context.RequestAborted))
                counts.Add(reader.IsDBNull(0) ? "NeverRun" : Status(reader.GetInt16(0)), reader.GetInt64(1));
            result.Add(new(team, type.Name, type.Description, CatalogMetadataSchema.From(type.MetadataSchema), counts));
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> ReadWorkflows(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization)
    {
        context.Response.Headers.CacheControl = "no-store";
        CatalogQuery filter;
        try { filter = CatalogQuery.Parse(context.Request.Query); }
        catch (ArgumentException error) { return Results.Problem(statusCode: 400, title: "Invalid catalogue filter", detail: error.Message); }
        var teams = await VisibleTeams(context, source, authorization, filter.Team);
        var types = teams.SelectMany(team => team.Types
            .Where(type => filter.Type is null || type.Name == filter.Type)
            .Select(type => team.Team + "/" + type.Name)).ToArray();
        var items = new List<CatalogWorkflow>();
        await using var query = source.CreateCommand("""
            SELECT w.id,w.type_id,av.id,av.semver,lv.id,lv.semver,
                COALESCE(av.manifest->'metadata',lv.manifest->'metadata')::text,
                latest.id,latest.status,latest.created_at,latest.started_at,latest.ended_at
            FROM workflows w
            JOIN workflow_versions lv ON lv.workflow_id=w.id AND lv.id=w.latest_version_id
            LEFT JOIN workflow_versions av ON av.workflow_id=w.id AND av.id=w.active_version_id
            LEFT JOIN LATERAL (
                SELECT r.id,r.status,r.created_at,r.started_at,r.ended_at FROM runs r WHERE r.workflow_id=w.id
                ORDER BY r.created_at DESC,r.id DESC LIMIT 1
            ) latest ON true
            WHERE w.type_id=ANY($1) AND w.deleted_at IS NULL AND w.id COLLATE "C" > $2 COLLATE "C"
            ORDER BY w.id COLLATE "C" LIMIT $3
            """);
        query.Parameters.AddWithValue(types);
        query.Parameters.AddWithValue(filter.AfterId ?? "");
        query.Parameters.AddWithValue(filter.Limit + 1);
        await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
        while (await reader.ReadAsync(context.RequestAborted))
        {
            var id = reader.GetString(0);
            var identity = id.Split('/');
            var metadata = JsonSerializer.Deserialize(reader.GetString(6), WorkflowManifestJsonContext.Default.WorkflowMetadata)
                ?? throw new InvalidDataException("A persisted catalogue workflow has no metadata.");
            items.Add(new(id, identity[0], identity[1], identity[2],
                reader.IsDBNull(2) ? null : new(reader.GetGuid(2), reader.GetString(3)),
                new(reader.GetGuid(4), reader.GetString(5)), metadata,
                reader.IsDBNull(7) ? null : new(reader.GetGuid(7), Status(reader.GetInt16(8)),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11))));
        }
        var more = items.Count > filter.Limit;
        if (more) items.RemoveAt(items.Count - 1);
        return Results.Ok(new CatalogWorkflowPage(items, more ? CatalogQuery.Cursor(items[^1].Id) : null));
    }

    private static async Task<List<TeamRegistry>> VisibleTeams(HttpContext context, NpgsqlDataSource source,
        IAuthorizationService authorization, string? team = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        var registries = new List<TeamRegistry>();
        await using (var query = source.CreateCommand("""
            SELECT registry::text FROM teams WHERE ($1::text IS NULL OR id=$1) ORDER BY id COLLATE "C"
            """))
        {
            query.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)team ?? DBNull.Value);
            await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
            while (await reader.ReadAsync(context.RequestAborted))
                registries.Add(JsonSerializer.Deserialize(reader.GetString(0), TeamRegistryJsonContext.Default.TeamRegistry)
                    ?? throw new InvalidDataException("A persisted team registry is null."));
        }
        var visible = new List<TeamRegistry>();
        foreach (var registry in registries)
        {
            var teamAllowed = await Allowed(TeamResourceKind.Team, registry.Team);
            var types = new List<WorkflowTypeDefinition>();
            foreach (var type in registry.Types)
                if (teamAllowed || await Allowed(TeamResourceKind.Type, registry.Team + "/" + type.Name))
                    types.Add(type with { RoleBindings = [] });
            if (!teamAllowed && types.Count == 0) continue;
            visible.Add(context.User.IsInRole(AuthExtensions.Admin) ? registry : registry with
            {
                RoleBindings = [], Types = [.. types], Presentation = teamAllowed ? registry.Presentation : null
            });
        }
        return visible;

        async Task<bool> Allowed(TeamResourceKind kind, string id) =>
            (await authorization.AuthorizeAsync(context.User, new TeamResource(kind, id, context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded;
    }

    private static string Status(short value) => Enum.IsDefined((RunStatus)value)
        ? ((RunStatus)value).ToString() : throw new InvalidDataException("A catalogue run has an unknown status.");
}
