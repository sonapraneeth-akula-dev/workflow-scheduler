using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class SchedulesEndpoints
{
    public static IEndpointRouteBuilder MapSchedulesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/schedules/{id:guid}/run-now", RunsEndpoints.Trigger).RequireAuthorization()
            .AddEndpointFilter(AuthorizeRunNow)
            .AddEndpointFilter<IdempotencyFilter>();
        return endpoints;
    }

    private static async ValueTask<object?> AuthorizeRunNow(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!Guid.TryParse(context.Request.RouteValues["id"]?.ToString(), out var scheduleId) || scheduleId == Guid.Empty)
            return Results.Problem(statusCode: 404, title: "Schedule not found");
        if (context.Request.ContentLength > 4096)
            return Results.Problem(statusCode: 413, title: "Run-now request exceeds its byte limit");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = 4096;
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            if (!context.Request.HasJsonContentType())
                return Results.Problem(statusCode: 415, title: "A JSON request content type is required");
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Any())
                    return BodyNotEmpty();
            }
            catch (JsonException) { return BodyNotEmpty(); }
            catch (BadHttpRequestException error) when (error.StatusCode == 413)
            {
                return Results.Problem(statusCode: 413, title: "Run-now request exceeds its byte limit");
            }
        }
        var source = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
        string workflowId;
        string? version;
        string variablesJson;
        Guid? seriesId;
        bool deleted;
        await using (var query = source.CreateCommand("""
            SELECT s.workflow_id,s.version_pin,s.variables::text,r.id,
                s.deleted_at IS NOT NULL OR w.deleted_at IS NOT NULL
            FROM schedules s JOIN workflows w ON w.id=s.workflow_id
            LEFT JOIN run_series r ON r.schedule_id=s.id AND r.workflow_id=s.workflow_id WHERE s.id=$1
            """))
        {
            query.Parameters.AddWithValue(scheduleId);
            await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
            if (!await reader.ReadAsync(context.RequestAborted))
                return Results.Problem(statusCode: 404, title: "Schedule not found");
            workflowId = reader.GetString(0);
            version = reader.IsDBNull(1) ? null : reader.GetString(1);
            variablesJson = reader.GetString(2);
            seriesId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            deleted = reader.GetBoolean(4);
        }
        var separator = workflowId.LastIndexOf('/');
        if (separator < 1) throw new InvalidDataException("The schedule has an invalid workflow identity.");
        var allowed = await context.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Type, workflowId[..separator], context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Operator));
        if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "Forbidden", detail: "A triggering principal object ID is required.");
        if (seriesId is null) throw new InvalidDataException("The schedule has no permanent run series.");
        using var variables = JsonDocument.Parse(variablesJson);
        var request = new TriggerRunRequest(workflowId, version)
        {
            Variables = variables.RootElement.EnumerateObject().ToImmutableDictionary(property => property.Name,
                property => property.Value.Clone(), StringComparer.Ordinal)
        };
        // Fingerprint the requested instance, not its editable configuration.
        context.Features.Set(new AuthorizedRunRequest(request, actor, seriesId, JsonSerializer.SerializeToElement(scheduleId), deleted));
        return await next(invocation);
    }

    private static IResult BodyNotEmpty() => RunsEndpoints.Invalid("$",
        "Omit the body or supply an empty object. Run-now uses the schedule's variables, version and series without overrides.");
}
