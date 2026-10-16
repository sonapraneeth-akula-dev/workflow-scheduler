using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Reports;

namespace Wf.ControlPlane.Reports;

public static class ReportPublishEndpoints
{
    public const string MetadataHeader = ReportUploadMetadata.HeaderName;
    private const int MaximumMetadataBytes = 8192;

    public static IEndpointRouteBuilder MapReportPublishEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ReportUploadMetadata.EndpointPath + "/{id}", Publish).AllowAnonymous()
            .Accepts<byte[]>("application/octet-stream").Produces<ReportReceipt>(201)
            .Produces(403).Produces(415).ProducesProblem(400).ProducesProblem(409).ProducesProblem(413).ProducesProblem(503)
            .WithDescription("Attempt-scoped report upload. Requires Authorization: Bearer <report capability>, " +
                "not an Entra user token. X-Wf-Report-Metadata is bounded base64url JSON metadata; the body is raw binary.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody!.Content!["application/octet-stream"].Schema =
                    new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = MetadataHeader, In = ParameterLocation.Header, Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static async Task<IResult> Publish(HttpContext context, string id,
        ReportUploadCapabilities capabilities, ReportStore store, ILogger<ReportStore> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } header ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(403);
        Wf.Engine.Core.Results.ResultIdentity attempt;
        try { attempt = capabilities.Read(header[7..]); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        if (!Guid.TryParseExact(id, "D", out var reportId) || reportId.Version != 7 || id != reportId.ToString("D"))
            return Results.Problem(statusCode: 400, title: "Invalid report ID");
        if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(415);
        try
        {
            var values = context.Request.Headers[MetadataHeader];
            if (values.Count != 1 || values[0] is not { Length: > 0 and <= 10924 } encoded)
                return InvalidMetadata();
            var bytes = WebEncoders.Base64UrlDecode(encoded);
            if (bytes.Length > MaximumMetadataBytes)
                return InvalidMetadata();
            var metadata = JsonSerializer.Deserialize(bytes, ReportJsonContext.Default.ReportUploadMetadata);
            if (metadata is null)
                return InvalidMetadata();
            metadata.Validate();
            if (metadata.SizeBytes > 5_000_000_000 || context.Request.ContentLength != metadata.SizeBytes)
                return Results.Problem(statusCode: 413, title: "Invalid report length");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = metadata.SizeBytes;
            var receipt = await store.PublishAsync(attempt, reportId, metadata, context.Request.Body, context.RequestAborted);
            return Results.Json(receipt, ReportJsonContext.Default.ReportReceipt, statusCode: 201);
        }
        catch (ReportLimitExceededException error)
        {
            return Results.Problem(statusCode: 413, title: "Report limit exceeded", detail: error.Message);
        }
        catch (ReportAttemptRejectedException) { return Results.StatusCode(403); }
        catch (ReportConflictException) { return Results.Problem(statusCode: 409, title: "Report ID already has different content"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            return Results.Problem(statusCode: 413, title: "Report payload exceeds its declared length");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
        {
            return InvalidMetadata();
        }
        catch (InvalidDataException)
        {
            return Results.Problem(statusCode: 400, title: "Report bytes do not match the declared size and SHA-256");
        }
        catch (Exception error) when (error is NpgsqlException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Report publication failed for run {RunId}.", attempt.RunId);
            return Results.Problem(statusCode: 503, title: "Report storage unavailable");
        }
    }

    private static IResult InvalidMetadata() => Results.Problem(statusCode: 400, title: "Invalid report metadata");
}
