using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.ControlPlane.Engine.Agents;

namespace Wf.ControlPlane.Api.Machines;

public sealed record AgentDrainRequest(int MaxDrainSeconds, string Reason, bool RequireResumable = false);
public sealed record AgentResumeRequest(Guid DrainId, string Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AgentDrainRequest))]
[JsonSerializable(typeof(AgentDrainRecord))]
[JsonSerializable(typeof(AgentResumeRequest))]
public partial class AgentDrainJsonContext : JsonSerializerContext;

internal static class AgentDrainEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{id}/drain", Request).Accepts<AgentDrainRequest>("application/json")
            .Produces<AgentDrainRecord>(202).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503)
            .WithDescription("Global Admin only. Stops new dispatches durably, including across reconnects. " +
                "maxDrainSeconds is 1..86400; reason is required (up to 1024 UTF-8 bytes). " +
                "Running tasks may finish until the deadline, then unfinished attempts are physically stopped and " +
                "re-placed using their original retry and placement policies. Released requires a current-session " +
                "physical acknowledgement, never a timer. The machine remains Draining after release. " +
                "Repeating the request returns the original drain without changing its deadline. Legacy agents return 409. " +
                "Optional requireResumable=true also requires process identity support before beginning maintenance.");
        group.MapGet("/{id}/drain", Read).Produces<AgentDrainRecord>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503)
            .WithDescription("Global Admin drain status: Draining or physically Released. " +
                "Released does not make the machine dispatchable; no automatic re-enable.");
        group.MapPost("/{id}/resume", Resume).Accepts<AgentResumeRequest>("application/json")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503)
            .WithDescription("Global Admin only. Re-enables an explicitly drained machine after physical release and " +
                "a fresh, idle, restarted agent process has registered. A network reconnect alone is insufficient. " +
                "Requires the exact drainId and a reason (up to 1024 UTF-8 bytes), a current heartbeat, no live reservations " +
                "and no assigned attempts. Legacy agents without process identity cannot resume. " +
                "A repeat with no current drain on an already healthy machine is a no-op; another drain identity returns 409. " +
                "The status change and before/after audit commit atomically.");
    }

    private static bool MachineId(string text, out Guid id) =>
        Guid.TryParseExact(text, "D", out id) && id != Guid.Empty && text == id.ToString("D");

    private static async Task<IResult> Request(string id, HttpContext context, AgentDrainService drains)
    {
        Headers(context);
        if (!MachineId(id, out var machine) || context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Invalid machine identity or query");
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "A valid audit principal is required");
        if (!context.Request.HasJsonContentType())
            return Results.Problem(statusCode: 415, title: "JSON content is required");
        const int maximumBytes = 4096;
        if (context.Request.ContentLength > maximumBytes)
            return Results.Problem(statusCode: 413, title: "Drain request is too large");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBytes;
        try
        {
            var request = await JsonSerializer.DeserializeAsync(context.Request.Body,
                AgentDrainJsonContext.Default.AgentDrainRequest, context.RequestAborted)
                ?? throw new JsonException("Drain request is missing.");
            var record = await drains.RequestAsync(machine, request.MaxDrainSeconds, request.Reason,
                actor.ToString("D"), context.User.HasClaim("idtyp", "app") ? "App" : "User", context.RequestAborted,
                request.RequireResumable);
            context.Response.Headers.Location = $"/api/v1/machines/{id}/drain";
            return Results.Json(record, AgentDrainJsonContext.Default.AgentDrainRecord, statusCode: 202);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return Results.Problem(statusCode: 400, title: "Invalid drain request"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return Results.Problem(statusCode: 413, title: "Drain request is too large"); }
        catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "Machine not found"); }
        catch (InvalidOperationException) { return Results.Problem(statusCode: 409, title: "Machine unavailable for drain"); }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Agent drain unavailable"); }
    }

    private static async Task<IResult> Read(string id, HttpContext context, AgentDrainService drains)
    {
        Headers(context);
        if (!MachineId(id, out var machine) || context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Invalid machine identity or query");
        try
        {
            var record = await drains.FindAsync(machine, context.RequestAborted);
            return record is null ? Results.Problem(statusCode: 404, title: "Drain not found") :
                Results.Json(record, AgentDrainJsonContext.Default.AgentDrainRecord);
        }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Agent drain unavailable"); }
    }

    private static async Task<IResult> Resume(string id, HttpContext context, AgentDrainService drains)
    {
        Headers(context);
        if (!MachineId(id, out var machine) || context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Invalid machine identity or query");
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "A valid audit principal is required");
        if (!context.Request.HasJsonContentType())
            return Results.Problem(statusCode: 415, title: "JSON content is required");
        const int maximumBytes = 4096;
        if (context.Request.ContentLength > maximumBytes)
            return Results.Problem(statusCode: 413, title: "Resume request is too large");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBytes;
        try
        {
            var request = await JsonSerializer.DeserializeAsync(context.Request.Body,
                AgentDrainJsonContext.Default.AgentResumeRequest, context.RequestAborted)
                ?? throw new JsonException("Resume request is missing.");
            await drains.ResumeAsync(machine, request.DrainId, request.Reason, actor.ToString("D"),
                context.User.HasClaim("idtyp", "app") ? "App" : "User", context.RequestAborted);
            return Results.NoContent();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return Results.Problem(statusCode: 400, title: "Invalid resume request"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return Results.Problem(statusCode: 413, title: "Resume request is too large"); }
        catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "Machine not found"); }
        catch (InvalidOperationException) { return Results.Problem(statusCode: 409, title: "Machine unavailable for resume"); }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Agent resume unavailable"); }
    }

    private static void Headers(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
