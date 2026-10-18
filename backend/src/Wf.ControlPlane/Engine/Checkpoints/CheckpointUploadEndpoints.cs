using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;

namespace Wf.ControlPlane.Engine.Checkpoints;

public static class CheckpointUploadEndpoints
{
    public static IEndpointRouteBuilder MapCheckpointUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut(CheckpointProtocol.EndpointPath, Upload).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> Upload(HttpContext context, CheckpointUploadCapabilities capabilities,
        CheckpointApplier checkpoints, ILogger<CheckpointApplier> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } header ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(403);
        try
        {
            var values = context.Request.Headers[CheckpointProtocol.MetadataHeader];
            if (values.Count != 1 || values[0] is not { Length: > 0 and <= 8192 } encoded)
                return Results.Problem(statusCode: 400, title: "Invalid checkpoint metadata");
            var saved = CheckpointSaved.Parser.ParseFrom(WebEncoders.Base64UrlDecode(encoded));
            _ = CheckpointProtocol.Validate(saved);
            var session = capabilities.Read(header[7..], saved);
            if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(415);
            if (context.Request.ContentLength != checked((long)saved.SizeBytes))
                return Results.Problem(statusCode: 413, title: "Invalid checkpoint length");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = checked((long)saved.SizeBytes);
            await checkpoints.UploadAsync(session, saved, context.Request.Body, context.RequestAborted);
            return Results.StatusCode(201);
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return Results.Problem(statusCode: 413, title: "Checkpoint exceeds its declared length"); }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidProtocolBufferException or InvalidDataException)
        { return Results.Problem(statusCode: 400, title: "Invalid checkpoint metadata or JSON state"); }
        catch (Exception error) when (error is NpgsqlException or IOException or InvalidOperationException)
        {
            logger.LogError(error, "Checkpoint blob upload failed.");
            return Results.Problem(statusCode: 503, title: "Checkpoint storage unavailable");
        }
    }
}
