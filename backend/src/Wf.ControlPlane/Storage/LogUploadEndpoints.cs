using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.Contracts.Registration;

namespace Wf.ControlPlane.Storage;

/// <summary>Attempt-scoped acquisition capability, never an OS path. Only the engine issues it. It permits
/// recovery through deadline + 24h; actual immutable object PUT grants expire after ten minutes.</summary>
public sealed class LogUploadCapabilities : IDisposable
{
    public const long MaximumObjectBytes = 16 * 1024 * 1024;
    private readonly byte[] key;
    private readonly TimeProvider clock;
    private bool disposed;

    public LogUploadCapabilities(byte[] hmacKey, TimeProvider clock)
    {
        if (hmacKey.Length < 32) throw new ArgumentException("Log capability key needs at least 256 bits.");
        key = hmacKey.ToArray();
        this.clock = clock;
    }

    public UploadGrant Create(AttemptRef attempt, DateTimeOffset deadline)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var expires = deadline.AddDays(1);
        var payload = System.Text.Encoding.UTF8.GetBytes(LogIndex.PrefixFor(attempt) + "|" + expires.UtcTicks);
        var token = WebEncoders.Base64UrlEncode(payload) + "." +
            WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload));
        return new UploadGrant
        {
            Kind = "logs", UploadUri = "/api/v1/log-upload-grants/" + token,
            MaxBytes = MaximumObjectBytes, ExpiresAt = Timestamp.FromDateTimeOffset(expires)
        };
    }

    public string Read(string token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > 1024) throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = WebEncoders.Base64UrlDecode(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, bytes), WebEncoders.Base64UrlDecode(parts[1])))
                throw new FormatException();
            var fields = System.Text.Encoding.UTF8.GetString(bytes).Split('|');
            if (fields.Length != 2 || !long.TryParse(fields[1], out var ticks) || ticks <= clock.GetUtcNow().UtcTicks)
                throw new FormatException();
            // Validate canonical paths through the existing storage trust boundary.
            _ = new BlobContent(BlobKind.Logs, new string('0', 64), 0, fields[0] + "index.json");
            return fields[0];
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        { throw new UnauthorizedAccessException("Invalid or expired attempt log capability."); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }
}

public static class LogUploadEndpoints
{
    public static IEndpointRouteBuilder MapLogUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/log-upload-grants/{capability}", Grant).AllowAnonymous();
        endpoints.MapPut("/api/v1/log-uploads/{grant}", Upload).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> Grant(HttpContext context, string capability, LogUploadCapabilities capabilities,
        BlobUploadGrants grants, TimeProvider clock)
    {
        try
        {
            var prefix = capabilities.Read(capability);
            Limit(context, 4096);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            var request = await context.Request.ReadFromJsonAsync(LogJsonContext.Default.LogUploadRequest, context.RequestAborted);
            if (request is null || request.Size < 0 || request.Size > LogUploadCapabilities.MaximumObjectBytes)
                return Results.StatusCode(413);
            var content = new BlobContent(BlobKind.Logs, request.Sha256, request.Size, prefix + request.Name);
            var lifetime = TimeSpan.FromMinutes(10);
            return Results.Json(new UploadGrantResponse(false,
                context.Request.PathBase + "/api/v1/log-uploads/" + grants.Create(content, content.SizeBytes, lifetime),
                content.SizeBytes, clock.GetUtcNow().Add(lifetime)), RegistrationJsonContext.Default.UploadGrantResponse);
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (Exception error) when (error is JsonException or ArgumentException) { return Results.StatusCode(400); }
    }

    private static async Task<IResult> Upload(HttpContext context, string grant, BlobUploadGrants grants, IBlobStore blobs,
        IServiceProvider services)
    {
        try
        {
            var content = grants.Read(grant);
            if (content.Kind != BlobKind.Logs || content.SizeBytes > LogUploadCapabilities.MaximumObjectBytes)
                return Results.StatusCode(403);
            if (context.Request.ContentLength > content.SizeBytes) return Results.StatusCode(413);
            Limit(context, content.SizeBytes);
            await blobs.UploadAsync(content, context.Request.Body, context.RequestAborted);
            if (content.Key.EndsWith("/index.json", StringComparison.Ordinal) &&
                services.GetService<Wf.ControlPlane.Live.LiveLogRelay>() is { } logs)
                await logs.IndexUploadedAsync(content.Key[..^"index.json".Length], context.RequestAborted);
            return Results.StatusCode(201);
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (InvalidDataException) { return Results.StatusCode(400); }
    }

    private static void Limit(HttpContext context, long bytes)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = bytes;
    }
}
