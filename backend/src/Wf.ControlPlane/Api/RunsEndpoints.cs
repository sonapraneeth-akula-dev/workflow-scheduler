using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Registry;
using Wf.Engine.Core;
using Wf.Validation;

namespace Wf.ControlPlane.Api;

internal sealed record AuthorizedRunRequest(TriggerRunRequest Request, Guid Actor, Guid? SeriesId = null,
    JsonElement? IdempotencyRequest = null, bool ScheduleDeleted = false);

public static class RunsEndpoints
{
    private const long MaximumRequestBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapRunsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapRunAbortEndpoint();
        endpoints.MapRerunEndpoint();
        endpoints.MapTaskOutputsEndpoints();
        endpoints.MapReservationQueueEndpoints();
        endpoints.MapPost("/api/v1/runs/placement-options", PlacementOptions).RequireAuthorization()
            .Accepts<TriggerRunRequest>("application/json").Produces<PlacementOptionsResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(503)
            .AddEndpointFilter(AuthorizeRequest);
        endpoints.MapPost("/api/v1/runs", Trigger).RequireAuthorization()
            .Accepts<TriggerRunRequest>("application/json").Produces<TriggerRunResponse>(202)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key", In = ParameterLocation.Header, Required = false,
                    Description = "Optional retry key: one value of 1 to 200 visible ASCII characters without whitespace.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 200 }
                });
                return Task.CompletedTask;
            })
            .AddEndpointFilter(AuthorizeRequest)
            .AddEndpointFilter<IdempotencyFilter>();
        return endpoints;
    }

    private static async ValueTask<object?> AuthorizeRequest(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasJsonContentType())
            return Results.Problem(statusCode: 415, title: "A JSON request content type is required");
        if (context.Request.ContentLength > MaximumRequestBytes) return TooLarge();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = MaximumRequestBytes;
        TriggerRunRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(RunJsonContext.Default.TriggerRunRequest,
                context.RequestAborted);
        }
        catch (JsonException error) { return Invalid(error.Path ?? "$", "Supply a valid run request with only supported properties."); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
        if (request is null) return Invalid("$", "Supply a run request.");
        if (request.Variables is null) return Invalid("$.variables", "Supply a JSON object of variable values, or omit variables.");
        if (PlacementOverrideValidator.Invalid(request.Placement) is { } invalidPlacement)
            return Invalid("$.placement", invalidPlacement);
        if (!WorkflowIdentity.TryGetTypeScope(request.WorkflowId, out var typeScope))
            return Invalid("$.workflowId", "Use an exact team/type/name identity, with at most 128 Unicode characters per segment.");
        var allowed = await context.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Type, typeScope, context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Operator));
        if (!allowed.Succeeded)
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "Forbidden", detail: "A triggering principal object ID is required.");
        context.Features.Set(new AuthorizedRunRequest(request, actor));
        return await next(invocation);
    }

    internal static Task<IResult> Trigger(HttpContext context,
        ActiveVersionCache versions, NpgsqlDataSource source, CommandInbox inbox, TimeProvider time) =>
        Accept(context, versions, source, inbox, time, false);

    private static Task<IResult> PlacementOptions(HttpContext context,
        ActiveVersionCache versions, NpgsqlDataSource source, CommandInbox inbox, TimeProvider time) =>
        Accept(context, versions, source, inbox, time, true);

    private static async Task<IResult> Accept(HttpContext context,
        ActiveVersionCache versions, NpgsqlDataSource source, CommandInbox inbox, TimeProvider time, bool optionsOnly)
    {
        var authorized = context.Features.Get<AuthorizedRunRequest>()
            ?? throw new InvalidOperationException("A run request must be authorized before acceptance.");
        var request = authorized.Request;
        var actor = authorized.Actor;
        if (authorized.ScheduleDeleted) return Results.Problem(statusCode: 404, title: "Schedule not found");
        if (!versions.IsReady) return Unavailable();
        SelectedWorkflowVersion selected;
        try
        {
            selected = await WorkflowVersionResolver.ResolveAsync(versions, source, request.WorkflowId,
                request.Version, authorized.SeriesId is not null, context.RequestAborted);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException) { return Unavailable(); }
        catch (ArgumentException) { return Invalid("$.version", "Use an exact workflow version UUID or SemVer 2.0.0."); }
        catch (FormatException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid schedule version pin",
                extensions: new Dictionary<string, object?>
                {
                    ["errors"] = new[] { RuleCatalog.ScheduleVersionPin.CreateError("schedule", "$.schedule.versionPin") }
                });
        }
        var (versionId, manifest) = selected;
        var variables = VariableBinder.Resolve(manifest.Variables, request.Variables,
            path: authorized.SeriesId is null ? "$.variables" : "$.schedule.variables", cancellationToken: context.RequestAborted);
        if (!variables.IsValid)
            return Results.Problem(statusCode: 400, title: "Run validation failed",
                extensions: new Dictionary<string, object?> { ["errors"] = variables.Errors });
        var selectorTransaction = context.Features.Get<NpgsqlTransaction>();
        await using (var connection = selectorTransaction is null ? await source.OpenConnectionAsync(context.RequestAborted) : null)
        {
            if (optionsOnly)
                return await PlacementOverrideValidator.OptionsAsync(
                    selectorTransaction?.Connection ?? connection!, selectorTransaction, manifest,
                    VariableBinder.Bind(manifest.Variables, variables.Values), time.GetUtcNow(), context.RequestAborted);
            var selectors = await MachineSelectorInventory.ValidateAsync(
                selectorTransaction?.Connection ?? connection ?? throw new InvalidOperationException("A trigger transaction has no connection."),
                selectorTransaction, manifest,
                VariableBinder.Bind(manifest.Variables, variables.Values), context.RequestAborted);
            if (!selectors.IsEmpty)
                return Results.Problem(statusCode: 400, title: "Run selector validation failed",
                    extensions: new Dictionary<string, object?> { ["errors"] = selectors });
            var placementErrors = await PlacementOverrideValidator.ValidateAsync(
                selectorTransaction?.Connection ?? connection!, selectorTransaction, manifest,
                VariableBinder.Bind(manifest.Variables, variables.Values), request.Placement, time.GetUtcNow(),
                context.RequestAborted);
            if (!placementErrors.IsEmpty)
                return Results.Problem(statusCode: 400, title: "Run placement validation failed",
                    extensions: new Dictionary<string, object?> { ["errors"] = placementErrors });
        }
        var now = time.GetUtcNow();
        var runId = Guid.CreateVersion7(now);
        var run = new RunState(runId, manifest.Nodes, manifest.Metadata, _ => { }, variables.Values);
        var command = new TriggerRunCommand(run.Snapshot with { Placement = request.Placement }, request.WorkflowId,
            versionId, now.Add(manifest.RunTimeout), authorized.SeriesId);
        if (context.Features.Get<NpgsqlTransaction>() is { } transaction)
            await inbox.TriggerAsync(command, actor.ToString("D"), transaction, context.RequestAborted);
        else
            await inbox.TriggerAsync(command, actor.ToString("D"), context.RequestAborted);
        return Results.Json(new TriggerRunResponse(runId), RunJsonContext.Default.TriggerRunResponse, statusCode: 202);
    }

    internal static IResult Invalid(string path, string hint) => Results.Problem(statusCode: 400,
        title: "Invalid run request", extensions: new Dictionary<string, object?>
        {
            ["errors"] = new[] { new ValidationError("RUN001", "request", path, "The run request does not match its contract.", hint) }
        });

    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "Run request exceeds its byte limit");
    private static IResult NotFound() => Results.Problem(statusCode: 404, title: "Workflow version not found");
    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Workflow versions are synchronizing");
}
