using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wf.Contracts.Registration;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Reports;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Registration;

public static class RegistrationEndpoints
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);
    private const long MaximumRegistrationBytes = 8 * 1024 * 1024;

    public static IServiceCollection AddWorkflowRegistrations(this IServiceCollection services, IConfiguration configuration)
    {
        byte[] key;
        try { key = Convert.FromBase64String(configuration["Storage:UploadHmacKey"] ?? ""); }
        catch (FormatException) { throw new InvalidOperationException("Storage:UploadHmacKey must be a base64-encoded key of at least 256 random bits."); }
        if (key.Length < 32)
            throw new InvalidOperationException("Storage:UploadHmacKey must be a base64-encoded key of at least 256 random bits.");
        var root = configuration["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "wf-data", "blobs");
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RegistryWriter>();
        services.TryAddSingleton<IBlobStore>(provider =>
            new FileSystemBlobStore(root, key, timeProvider: provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new BlobUploadGrants(key, RegistrationService.MaximumPackageBytes,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new LogUploadCapabilities(key, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new ReportUploadCapabilities(key, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ReportStore>();
        services.AddSingleton(provider => new Wf.ControlPlane.Artifacts.ArtifactUploadCapabilities(key, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Wf.ControlPlane.Artifacts.ArtifactGrantService>();
        services.AddSingleton<Wf.ControlPlane.Engine.ResourceSampleSink>();
        services.AddSingleton(provider => new Wf.ControlPlane.Api.ResourceReadCursors(key, provider.GetRequiredService<TimeProvider>()));
        services.AddHostedService<Wf.ControlPlane.Engine.ResourceRetentionService>();
        services.AddSingleton<RegistrationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/registrations", Register).RequireAuthorization(AuthExtensions.Registrar);
        endpoints.MapPost("/api/v1/upload-grants", Grant).RequireAuthorization(AuthExtensions.Registrar);
        endpoints.MapPut("/api/v1/uploads/{grant}", Upload).AllowAnonymous();
        endpoints.MapLogUploadEndpoints();
        endpoints.MapReportPublishEndpoints();
        Wf.ControlPlane.Artifacts.ArtifactEndpoints.MapArtifactEndpoints(endpoints);
        Wf.ControlPlane.Api.ResourceMetricsEndpoints.MapResourceMetricsEndpoints(endpoints);
        return endpoints;
    }

    private static async Task<IResult> Register(HttpContext context, RegistrationService service)
    {
        if (!context.Request.HasJsonContentType()) return JsonRequired();
        Limit(context, MaximumRegistrationBytes);
        try
        {
            var request = await context.Request.ReadFromJsonAsync(RegistrationJsonContext.Default.RegistrationRequest,
                context.RequestAborted);
            if (request is null) return Invalid();
            var actor = context.User.FindFirstValue("oid");
            if (!Guid.TryParse(actor, out var principal) || principal == Guid.Empty)
                return Results.Problem(statusCode: 403, title: "Forbidden", detail: "A registering principal object ID is required.");
            var response = await service.RegisterAsync(request, principal.ToString("D"), context.RequestAborted);
            return Results.Json(response, RegistrationJsonContext.Default.RegistrationResponse, statusCode: response.Changed ? 201 : 200);
        }
        catch (JsonException) { return Invalid(); }
        catch (RegistrationValidationException error) { return ValidationProblem(error.Errors); }
        catch (RegistryValidationException error) { return ValidationProblem(error.Errors); }
        catch (RegistrationConflictException error)
        {
            return Results.Problem(statusCode: 409, title: "Registration conflict", detail: error.Message,
                extensions: error.Errors.IsEmpty ? null : new Dictionary<string, object?> { ["errors"] = error.Errors });
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Problem(statusCode: 409, title: "Registration conflict",
                detail: "The registry update would remove a resource that is still referenced.");
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
    }

    private static async Task<IResult> Grant(HttpContext context, IBlobStore blobs, BlobUploadGrants grants, TimeProvider time)
    {
        if (!context.Request.HasJsonContentType()) return JsonRequired();
        Limit(context, 4096);
        try
        {
            var request = await context.Request.ReadFromJsonAsync(RegistrationJsonContext.Default.UploadGrantRequest,
                context.RequestAborted);
            if (request is null || request.Kind != "package" || request.Size <= 0) return Invalid();
            if (request.Size > RegistrationService.MaximumPackageBytes) return TooLarge();
            BlobContent content;
            try { content = new(BlobKind.Packages, request.Sha256, request.Size); }
            catch (ArgumentException) { return Invalid(); }
            bool exists;
            try
            {
                await blobs.DownloadAsync(content, Stream.Null, context.RequestAborted);
                exists = true;
            }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            catch (Azure.RequestFailedException error) when (error.Status == 404) { exists = false; }
            if (exists)
                return Results.Json(new UploadGrantResponse(true), RegistrationJsonContext.Default.UploadGrantResponse);
            var grant = grants.Create(content, content.SizeBytes, GrantLifetime);
            var response = new UploadGrantResponse(false, context.Request.PathBase + "/api/v1/uploads/" + grant,
                content.SizeBytes, time.GetUtcNow().Add(GrantLifetime));
            return Results.Json(response, RegistrationJsonContext.Default.UploadGrantResponse, statusCode: 201);
        }
        catch (JsonException) { return Invalid(); }
        catch (InvalidDataException) { return Results.Problem(statusCode: 409, title: "Stored package integrity check failed"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
    }

    private static async Task<IResult> Upload(HttpContext context, string grant, IBlobStore blobs, BlobUploadGrants grants)
    {
        try
        {
            var content = grants.Read(grant);
            if (content.Kind != BlobKind.Packages || content.SizeBytes > RegistrationService.MaximumPackageBytes)
                return Results.Problem(statusCode: 403, title: "Invalid upload grant");
            if (context.Request.ContentLength > content.SizeBytes) return TooLarge();
            Limit(context, content.SizeBytes);
            await blobs.UploadAsync(content, context.Request.Body, context.RequestAborted);
            return Results.StatusCode(201);
        }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 403, title: "Invalid or expired upload grant"); }
        catch (InvalidDataException) { return Results.Problem(statusCode: 400, title: "Upload length or SHA-256 mismatch"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
    }

    private static void Limit(HttpContext context, long bytes)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = bytes;
    }

    private static IResult ValidationProblem(IEnumerable<Wf.Validation.ValidationError> errors) =>
        Results.Problem(statusCode: 400, title: "Registration validation failed",
            extensions: new Dictionary<string, object?> { ["errors"] = errors });

    private static IResult Invalid() => Results.Problem(statusCode: 400, title: "Invalid registration request");
    private static IResult JsonRequired() => Results.Problem(statusCode: 415, title: "A JSON request content type is required");
    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "Upload or registration exceeds its byte limit");
}
