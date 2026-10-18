using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Artifacts;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Artifacts;

public static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ArtifactMetadata.GrantPath + "/{id}", Grant).AllowAnonymous()
            .Accepts<ArtifactMetadata>("application/json").Produces<ArtifactGrant>(201)
            .Produces(403).Produces(415).ProducesProblem(400).ProducesProblem(409).ProducesProblem(413).ProducesProblem(503)
            .WithDescription("Attempt-capability bearer authentication, not an Entra user token. " +
                "Reserves exact bytes against the durable 5,000,000,000-byte run budget before upload.");
        endpoints.MapPut(ArtifactMetadata.PublishPath + "/{id}", Publish).AllowAnonymous()
            .Accepts<byte[]>("application/octet-stream").Produces<ArtifactReceipt>(201)
            .Produces(403).Produces(415).ProducesProblem(400).ProducesProblem(409).ProducesProblem(413).ProducesProblem(503)
            .WithDescription("Raw, exact Content-Length upload against an admitted artifact grant. " +
                "Requires the dispatched attempt bearer capability; returns only after verified bytes and metadata commit.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody!.Content!["application/octet-stream"].Schema =
                    new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
                return Task.CompletedTask;
            });
        endpoints.MapDelete(ArtifactMetadata.GrantPath + "/{id}", Release).AllowAnonymous()
            .Produces(204).Produces(403).ProducesProblem(400).ProducesProblem(503);
        endpoints.MapGet("/api/v1/runs/{id}/artifacts", List).RequireAuthorization()
            .Produces<ArtifactSummary[]>().Produces(401).ProducesProblem(400).ProducesProblem(403).ProducesProblem(503);
        endpoints.MapGet("/api/v1/artifacts/{id}/content", Download).RequireAuthorization()
            .Produces<byte[]>(200, "application/octet-stream").Produces(401).ProducesProblem(400).ProducesProblem(403).ProducesProblem(503)
            .WithDescription("Fresh run-scoped Viewer authorization. Integrity-checked streamed attachment, never inline active content.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Responses!["200"].Content!["application/octet-stream"].Schema =
                    new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static Task<IResult> Grant(HttpContext context, string id, ArtifactUploadCapabilities capabilities,
        ArtifactGrantService service, ILogger<ArtifactGrantService> logger) =>
        Attempt(context, id, capabilities, logger, async (actor, artifactId) =>
        {
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 4096;
            var metadata = await context.Request.ReadFromJsonAsync(ArtifactJsonContext.Default.ArtifactMetadata,
                context.RequestAborted) ?? throw new ArgumentException("Metadata is required.");
            return Results.Json(await service.GrantAsync(actor, artifactId, metadata, context.RequestAborted),
                ArtifactJsonContext.Default.ArtifactGrant, statusCode: 201);
        });

    private static Task<IResult> Publish(HttpContext context, string id, ArtifactUploadCapabilities capabilities,
        ArtifactGrantService service, ILogger<ArtifactGrantService> logger) =>
        Attempt(context, id, capabilities, logger, async (actor, artifactId) =>
        {
            if (context.Request.ContentType != "application/octet-stream") return Results.StatusCode(415);
            if (context.Request.ContentLength is null or < 0 or > ArtifactMetadata.RunBudget)
                return Results.Problem(statusCode: 413, title: "Invalid artifact length");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = context.Request.ContentLength;
            return Results.Json(await service.PublishAsync(actor, artifactId, context.Request.Body,
                context.Request.ContentLength, context.RequestAborted), ArtifactJsonContext.Default.ArtifactReceipt, statusCode: 201);
        });

    private static Task<IResult> Release(HttpContext context, string id, ArtifactUploadCapabilities capabilities,
        ArtifactGrantService service, ILogger<ArtifactGrantService> logger) =>
        Attempt(context, id, capabilities, logger, async (actor, artifactId) =>
        {
            await service.ReleaseAsync(actor, artifactId, context.RequestAborted);
            return Results.NoContent();
        });

    private static async Task<IResult> Attempt(HttpContext context, string id, ArtifactUploadCapabilities capabilities,
        ILogger logger, Func<ArtifactActor, Guid, Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } bearer ||
            !bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(403);
        try
        {
            var actor = capabilities.Read(bearer[7..]);
            if (!Identity(id, out var artifactId)) return Results.Problem(statusCode: 400, title: "Invalid artifact ID");
            return await action(actor, artifactId);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ArtifactAttemptRejectedException)
        { return Results.StatusCode(403); }
        catch (ArtifactLimitExceededException) { return Results.Problem(statusCode: 413, title: "Artifact run budget exceeded"); }
        catch (Exception error) when (error is ArtifactConflictException or PostgresException { SqlState: "23505" })
        { return Results.Problem(statusCode: 409, title: "Artifact identity conflicts"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return Results.Problem(statusCode: 413, title: "Artifact payload exceeds declared length"); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException)
        { return Results.Problem(statusCode: 400, title: "Invalid artifact metadata or content integrity"); }
        catch (Exception error) when (error is NpgsqlException or IOException or InvalidOperationException or RequestFailedException)
        {
            logger.LogError(error, "Artifact operation failed.");
            return Unavailable();
        }
    }

    private static async Task<IResult> List(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, ILogger<ArtifactGrantService> logger)
    {
        if (!Identity(id, out var runId)) return Results.Problem(statusCode: 400, title: "Invalid run ID");
        try
        {
            if (!(await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            if (context.Request.QueryString.HasValue) return Results.Problem(statusCode: 400, title: "Invalid artifact query");
            context.Response.Headers.CacheControl = "no-store";
            await using var query = source.CreateCommand("""
                SELECT id,source_key,name,content_type,size_bytes,encode(sha256,'hex'),created_at FROM artifacts
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 ORDER BY created_at,id
                """);
            query.Parameters.AddWithValue(runId);
            await using var row = await query.ExecuteReaderAsync(context.RequestAborted);
            var results = new List<ArtifactSummary>();
            while (await row.ReadAsync(context.RequestAborted))
                results.Add(new(row.GetGuid(0), runId, row.GetString(1), row.GetString(2), row.GetString(3),
                    row.GetInt64(4), row.GetString(5), row.GetFieldValue<DateTimeOffset>(6)));
            return Results.Json(results.ToArray(), ArtifactJsonContext.Default.ArtifactSummaryArray);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
        {
            logger.LogError(error, "Artifact index unavailable.");
            return Unavailable();
        }
    }

    private static async Task<IResult> Download(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, IBlobStore blobs, ILogger<ArtifactGrantService> logger)
    {
        if (!Identity(id, out var artifactId) ||
            context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/artifacts/{id}/content")
            return Results.Problem(statusCode: 400, title: "Invalid artifact ID");
        try
        {
            Guid runId;
            await using (var lookup = source.CreateCommand("SELECT run_id FROM artifact_grants WHERE id=$1 AND committed"))
            {
                lookup.Parameters.AddWithValue(artifactId);
                if (await lookup.ExecuteScalarAsync(context.RequestAborted) is not Guid found)
                    return Results.Problem(TeamAuthorizationExtensions.Forbidden());
                runId = found;
            }
            if (!(await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, runId.ToString("D"), context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            if (context.Request.QueryString.HasValue) return Results.Problem(statusCode: 400, title: "Invalid artifact query");
            await using var query = source.CreateCommand("""
                SELECT name,size_bytes,encode(sha256,'hex'),blob_uri FROM artifacts
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND id=$2
                """);
            query.Parameters.AddWithValue(runId);
            query.Parameters.AddWithValue(artifactId);
            await using var row = await query.ExecuteReaderAsync(context.RequestAborted);
            if (!await row.ReadAsync(context.RequestAborted)) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            var content = new BlobContent(BlobKind.Artifacts, row.GetString(2), row.GetInt64(1));
            if (content.Key != row.GetString(3)) throw new InvalidDataException("Invalid artifact pointer.");
            return new DownloadResult(blobs, content, row.GetString(0), logger);
        }
        catch (Exception error) when (error is NpgsqlException or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            logger.LogError(error, "Artifact download lookup unavailable.");
            return Unavailable();
        }
    }

    private sealed class DownloadResult(IBlobStore blobs, BlobContent content, string name, ILogger logger) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(name);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; frame-ancestors 'none'";
            try { await blobs.DownloadAsync(content, context.Response.Body, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
            catch (Exception error) when (error is IOException or InvalidDataException or RequestFailedException or
                UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                logger.LogError(error, "Artifact download interrupted or failed checksum verification.");
                if (context.Response.HasStarted) context.Abort();
                else
                {
                    context.Response.Clear();
                    await Unavailable().ExecuteAsync(context);
                }
            }
        }
    }

    private static bool Identity(string id, out Guid value) =>
        Guid.TryParseExact(id, "D", out value) && value.Version == 7 && id == value.ToString("D");
    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Artifact storage unavailable");
}
