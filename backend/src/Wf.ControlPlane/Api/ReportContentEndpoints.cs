using Azure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Api;

public static class ReportContentEndpoints
{
    public const string ContentPolicy = "sandbox allow-scripts; frame-ancestors 'self'";
    private sealed class ContentRoute;
    private static readonly ContentRoute Route = new();

    public static IApplicationBuilder UseReportResponseHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var content = context.GetEndpoint()?.Metadata.GetMetadata<ContentRoute>() is not null;
            var success = content && context.Response.StatusCode == 200 && context.Items.ContainsKey(Route);
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (success) context.Response.Headers.Remove("X-Frame-Options");
            else context.Response.Headers.XFrameOptions = "DENY";
            if (content)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentSecurityPolicy = success ? ContentPolicy : "sandbox; frame-ancestors 'none'";
            }
            return Task.CompletedTask;
        });
        await next(context);
    });

    public static IEndpointRouteBuilder MapReportContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/reports/{id}/content", Read).RequireAuthorization().WithMetadata(Route)
            .Produces<byte[]>(200, "application/octet-stream")
            .ProducesProblem(400).Produces(401).ProducesProblem(403).ProducesProblem(503)
            .WithDescription("Viewer-scoped immutable report bytes, streamed and integrity checked. " +
                "HTML/SVG and direct navigation use an opaque-origin CSP sandbox. Unknown kinds download. " +
                "An interrupted or corrupt stream is aborted, never a successfully completed partial response.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Responses!["200"].Content!["application/octet-stream"].Schema =
                    new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, IBlobStore blobs, ILoggerFactory logging)
    {
        if (!Guid.TryParseExact(id, "D", out var reportId) || reportId.Version != 7 || id != reportId.ToString("D") ||
            context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/reports/{id}/content")
            return Results.Problem(statusCode: 400, title: "Invalid report identity");
        try
        {
            var token = context.RequestAborted;
            Guid runId;
            await using (var lookup = source.CreateCommand(
                "SELECT r.run_id FROM report_ids i JOIN reports r ON r.run_day=i.run_day AND r.id=i.id WHERE i.id=$1"))
            {
                lookup.Parameters.AddWithValue(reportId);
                if (await lookup.ExecuteScalarAsync(token) is not Guid found)
                    return Results.Problem(TeamAuthorizationExtensions.Forbidden());
                runId = found;
            }
            var permitted = await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, runId.ToString("D"), token),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!permitted.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            if (context.Request.QueryString.HasValue)
                return Results.Problem(statusCode: 400, title: "Invalid report query");
            string kind;
            BlobContent content;
            await using (var query = source.CreateCommand("""
                SELECT kind,encode(sha256,'hex'),size_bytes,blob_uri FROM reports
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND id=$2
                """))
            {
                query.Parameters.AddWithValue(runId);
                query.Parameters.AddWithValue(reportId);
                await using var row = await query.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) throw new InvalidDataException("Report disappeared.");
                kind = row.GetString(0);
                var key = $"reports/{runId:D}/{reportId:D}";
                if (row.GetString(3) != key) throw new InvalidDataException("Invalid stored report identity.");
                content = new(BlobKind.Reports, row.GetString(1), row.GetInt64(2), key);
            }
            var (mime, extension, inline) = ContentType(kind);
            return new ContentResult(blobs, content, mime,
                $"{(inline ? "inline" : "attachment")}; filename=\"report-{reportId:D}.{extension}\"",
                logging.CreateLogger("Wf.ReportContent"));
        }
        catch (Exception error) when (error is NpgsqlException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            logging.CreateLogger("Wf.ReportContent").LogError(error, "Report content lookup unavailable.");
            return Unavailable();
        }
    }

    public static (string Mime, string Extension, bool Inline) ContentType(string kind) => kind switch
    {
        "html" or "text/html" => ("text/html; charset=utf-8", "html", true),
        "svg" or "image/svg+xml" => ("image/svg+xml", "svg", true),
        "png" or "image/png" => ("image/png", "png", true),
        "jpeg" or "jpg" or "image/jpeg" => ("image/jpeg", "jpg", true),
        "pdf" or "application/pdf" => ("application/pdf", "pdf", true),
        "json" or "chart" or "application/json" => ("application/json; charset=utf-8", "json", true),
        "csv" or "table" or "text/csv" => ("text/plain; charset=utf-8", "txt", true),
        "markdown" or "text" or "diff" or "log" or "text/plain" => ("text/plain; charset=utf-8", "txt", true),
        _ => ("application/octet-stream", "bin", false)
    };

    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Report content unavailable");

    private sealed class ContentResult(IBlobStore blobs, BlobContent content, string mime, string disposition,
        ILogger logger) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType = mime;
            context.Response.Headers.ContentDisposition = disposition;
            context.Items[Route] = true;
            try
            {
                // No Content-Length: a final hash failure must not look like a complete successful body.
                await blobs.DownloadAsync(content, context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
            catch (Exception error) when (error is IOException or InvalidDataException or RequestFailedException or
                UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                logger.LogError(error, "Report content stream unavailable or failed integrity verification.");
                if (context.Response.HasStarted) context.Abort();
                else
                {
                    context.Items.Remove(Route);
                    context.Response.Clear();
                    await Unavailable().ExecuteAsync(context);
                }
            }
        }
    }
}
