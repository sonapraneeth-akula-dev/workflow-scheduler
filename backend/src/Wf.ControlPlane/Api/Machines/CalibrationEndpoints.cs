using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Wf.Contracts;
using Wf.ControlPlane.Engine.Agents;
using Npgsql;

namespace Wf.ControlPlane.Api.Machines;

public sealed record CalibrationRequest(CapacityProfile Profile);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(CalibrationRequest))]
[JsonSerializable(typeof(CalibrationRecord))]
public partial class CalibrationJsonContext : JsonSerializerContext;

internal static class CalibrationEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{id}/calibrate", Request).Accepts<CalibrationRequest>("application/json")
            .Produces<CalibrationRecord>(202).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Global Admin only. Queues durable calibration for one canonical profile on a fresh, idle, unreserved " +
                "machine supporting calibration commands. Returns 409 while reserved, busy, draining, stale or unsupported. " +
                "Machine stays Draining until its current session reports a terminal result, including after deadline expiry. " +
                "Positive successful req/s is Calibrated only before the server deadline; Manual capacity always wins. " +
                "Native agents currently report Unsupported: the load executor is deferred; no capacity is fabricated.");
        group.MapGet("/{id}/calibrations/{calibrationId}", Read)
            .Produces<CalibrationRecord>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Global Admin calibration status, result and whether the reported capacity was applied. " +
                "Failed includes Unsupported, TimedOut, Busy or Failed; failures never change capacity.");
    }

    private static bool MachineId(string text, out Guid id) =>
        Guid.TryParseExact(text, "D", out id) && id != Guid.Empty && text == id.ToString("D");

    private static async Task<IResult> Request(string id, HttpContext context, CalibrationService calibrations)
    {
        Headers(context);
        if (!MachineId(id, out var machine) || context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Invalid machine identity or query");
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "A valid audit principal is required");
        if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 415, title: "JSON content is required");
        const int maximumBytes = 64 * 1024;
        if (context.Request.ContentLength > maximumBytes)
            return Results.Problem(statusCode: 413, title: "Calibration request is too large");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBytes;
        CalibrationRequest request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(context.Request.Body, CalibrationJsonContext.Default.CalibrationRequest,
                context.RequestAborted) ?? throw new JsonException("Calibration request is missing.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return Results.Problem(statusCode: 400, title: "Invalid calibration profile"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return Results.Problem(statusCode: 413, title: "Calibration request is too large"); }
        try
        {
            var record = await calibrations.RequestAsync(machine, request.Profile, actor.ToString("D"),
                context.User.HasClaim("idtyp", "app") ? "App" : "User", context.RequestAborted);
            context.Response.Headers.Location = $"/api/v1/machines/{id}/calibrations/{record.Id:D}";
            return Results.Json(record, CalibrationJsonContext.Default.CalibrationRecord, statusCode: 202);
        }
        catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "Machine not found"); }
        catch (InvalidOperationException) { return Results.Problem(statusCode: 409, title: "Machine unavailable for calibration"); }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Calibration unavailable"); }
        catch (InvalidDataException) { return Results.Problem(statusCode: 500, title: "Committed calibration data is inconsistent"); }
    }

    private static async Task<IResult> Read(string id, string calibrationId, HttpContext context, CalibrationService calibrations)
    {
        Headers(context);
        if (!MachineId(id, out var machine) || !MachineId(calibrationId, out var calibration) || calibration.Version != 7 ||
            context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Invalid calibration identity or query");
        try
        {
            var record = await calibrations.FindAsync(machine, calibration, context.RequestAborted);
            return record is null ? Results.Problem(statusCode: 404, title: "Calibration not found") :
                Results.Json(record, CalibrationJsonContext.Default.CalibrationRecord);
        }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Calibration unavailable"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException)
        { return Results.Problem(statusCode: 500, title: "Committed calibration data is inconsistent"); }
    }

    private static void Headers(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
