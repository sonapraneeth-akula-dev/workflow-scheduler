using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;
using Wf.ControlPlane.Versions;
using Wf.Validation;

namespace Wf.ControlPlane.Api;

public static class VersionCloneEndpoint
{
    private static readonly WorkflowManifestJsonContext ManifestJson = new(new(WorkflowManifestJsonContext.Default.Options)
    {
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        AllowDuplicateProperties = false
    });

    internal static void MapVersionClone(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/clone", Clone)
            .RequireAuthorization()
            .Produces<byte[]>(200, contentType: "application/zip")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Download a ZIP scaffold from one exact immutable version UUID, including historical and soft-deleted sources. " +
                "Body: newName is a workflow leaf, targetType is an exact team/type scope. Fresh Viewer(source type) and Author(target type) " +
                "permissions are required. Existing targets (including deleted) conflict. No registration, activation or database mutation. " +
                "Only literal Workflow.Define/Type fluent declarations are supported; unsupported source returns 400. " +
                "Authored tasks and directives are preserved, version starts at 1.0.0, Metadata(\"ClonedFrom\") records source@UUID. " +
                "Schedules start empty and tests validate identity and lineage. Source is bounded, SHA-256/length verified and parsed only, " +
                "never compiled/executed by this endpoint. Target registry schema requirements must be reviewed before publishing.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // Manual bounded JSON parsing also returns ProblemDetails for unsupported media types.
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object, Required = new HashSet<string> { "newName", "targetType" },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["newName"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "New workflow leaf name." },
                                    ["targetType"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Exact team/type scope." }
                                }
                            }
                        }
                    }
                };
                operation.Responses!["200"].Content!["application/zip"].Schema =
                    new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
                return Task.CompletedTask;
            });

    private static async Task<IResult> Clone(HttpContext context, string team, string type, string workflow, string version,
        NpgsqlDataSource source, IAuthorizationService authorization, [FromServices] IBlobStore blobs,
        ILogger<VersionCloneRequest> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        var workflowId = team + "/" + type + "/" + workflow;
        if (!WorkflowIdentity.TryGetTypeScope(workflowId, out var scope) ||
            !Guid.TryParseExact(version, "D", out var id) || id == Guid.Empty || id.ToString("D") != version ||
            context.Request.QueryString.HasValue)
            return Problem(400, "Invalid immutable workflow version identity", "invalid_version_identity");
        if (!context.Request.HasJsonContentType())
            return Problem(415, "Use application/json", "invalid_clone_content_type");
        var token = context.RequestAborted;
        VersionCloneRequest request;
        string target;
        try
        {
            request = await ReadRequest(context.Request, token);
            target = request.TargetWorkflow(workflowId);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
        {
            return Problem(400, "Supply a different workflow leaf newName and exact team/type targetType", "invalid_clone_request");
        }
        try
        {
            var read = await authorization.AuthorizeAsync(context.User, new TeamResource(TeamResourceKind.Type, scope, token),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!read.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            var write = await authorization.AuthorizeAsync(context.User, new TeamResource(TeamResourceKind.Type, request.TargetType, token),
                TeamAuthorizationExtensions.Policy(RegistryRole.Author));
            if (!write.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            WorkflowManifest manifest;
            BlobContent content;
            await using (var query = source.CreateCommand("""
                SELECT semver,manifest::text,source_uri,source_sha256,source_size,
                    EXISTS(SELECT 1 FROM workflows WHERE id=$3)
                FROM workflow_versions WHERE workflow_id=$1 AND id=$2
                """))
            {
                query.Parameters.AddWithValue(workflowId);
                query.Parameters.AddWithValue(id);
                query.Parameters.AddWithValue(target);
                await using var reader = await query.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    return Problem(404, "Workflow version not found", "version_not_found");
                if (reader.GetBoolean(5)) return Problem(409, "Target workflow already exists", "clone_target_exists");
                manifest = JsonSerializer.Deserialize(reader.GetString(1), ManifestJson.WorkflowManifest)
                    ?? throw new InvalidDataException();
                if (manifest.Name != workflowId || manifest.Version != reader.GetString(0) ||
                    manifest.Metadata.Type != type || !Validator.Validate(manifest, cancellationToken: token).IsValid)
                    throw new InvalidDataException();
                content = new(BlobKind.Sources, Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(3)), reader.GetInt64(4));
                if (reader.GetString(2) != content.Key) throw new InvalidDataException();
            }
            var script = await VersionSourceReader.ReadAsync(blobs, content, token);
            var zip = CloneScaffolder.Create(script, manifest, id, request, token);
            return Results.File(zip, "application/zip", "workflow-clone.zip");
        }
        catch (UnsupportedCloneSourceException)
        {
            return Problem(400, "Source declaration is unsupported for safe cloning", "unsupported_clone_source");
        }
        catch (NpgsqlException)
        {
            logger.LogError("Workflow clone database is unavailable.");
            return Problem(503, "Workflow version data is unavailable", "version_data_unavailable");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or InvalidCastException)
        {
            logger.LogError("Workflow clone data is inconsistent.");
            return Problem(500, "Workflow version data is inconsistent", "version_data_inconsistent");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Azure.RequestFailedException)
        {
            logger.LogError("Workflow clone source storage is unavailable.");
            return Problem(503, "Workflow source data is unavailable", "version_source_unavailable");
        }
    }

    private static async Task<VersionCloneRequest> ReadRequest(HttpRequest request, CancellationToken token)
    {
        const int maximumBytes = 4096;
        if (request.ContentLength > maximumBytes) throw new InvalidDataException();
        var buffer = new byte[maximumBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > maximumBytes) throw new InvalidDataException();
        using var json = JsonDocument.Parse(buffer.AsMemory(0, count), new() { MaxDepth = 4 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        string? name = null, type = null;
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) throw new JsonException();
            switch (property.Name)
            {
                case "newName" when name is null: name = property.Value.GetString(); break;
                case "targetType" when type is null: type = property.Value.GetString(); break;
                default: throw new JsonException();
            }
        }
        return name is not null && type is not null ? new(name, type) : throw new JsonException();
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
