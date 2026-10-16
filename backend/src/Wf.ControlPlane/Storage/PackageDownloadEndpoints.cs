using System.Security.Cryptography;
using Azure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Wf.ControlPlane.Storage;

public static class PackageDownloadEndpoints
{
    public const string Grants = "workflow-package-download";

    public static IServiceCollection AddPackageDownloads(this IServiceCollection services, byte[] signingKey)
    {
        services.TryAddKeyedSingleton<BlobUploadGrants>(Grants, (provider, _) =>
        {
            var key = HMACSHA256.HashData(signingKey, "wf/package-download/v1"u8);
            try { return new(key, Registration.RegistrationService.MaximumPackageBytes, provider.GetRequiredService<TimeProvider>()); }
            finally { CryptographicOperations.ZeroMemory(key); }
        });
        return services;
    }

    public static IEndpointRouteBuilder MapPackageDownloads(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/packages/{capability}", Download).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }

    private static IResult Download(string capability,
        [FromKeyedServices(Grants)] BlobUploadGrants grants, IBlobStore blobs, ILoggerFactory logging)
    {
        try
        {
            var content = grants.Read(capability);
            return content.Kind == BlobKind.Packages
                ? new PackageResult(blobs, content, logging.CreateLogger("Wf.PackageDownloads"))
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
    }

    private sealed class PackageResult(IBlobStore blobs, BlobContent content, ILogger logger) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.ContentType = "application/zip";
            try { await blobs.DownloadAsync(content, context.Response.Body, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
            catch (Exception error) when (error is IOException or InvalidDataException or RequestFailedException or UnauthorizedAccessException)
            {
                logger.LogError(error, "Workflow package download failed or did not match its content hash.");
                if (context.Response.HasStarted) context.Abort();
                else
                {
                    context.Response.Clear();
                    await Results.Problem(statusCode: 503, title: "Workflow package unavailable").ExecuteAsync(context);
                }
            }
        }
    }
}
