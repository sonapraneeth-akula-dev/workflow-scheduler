using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.Engine.Core;
using Wf.Validation;
using Wf.Validation.SemVer;

namespace Wf.ControlPlane.Api;

public static class RunsEndpoints
{
    private const long MaximumRequestBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapRunsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/runs", Trigger).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> Trigger(HttpContext context, IAuthorizationService authorization,
        ActiveVersionCache versions, NpgsqlDataSource source, CommandInbox inbox, TimeProvider time)
    {
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
        var identity = request.WorkflowId.Split('/');
        if (identity.Length != 3 || identity.Any(part => string.IsNullOrWhiteSpace(part) || part.EnumerateRunes().Take(129).Count() > 128 ||
            part is "." or ".." || part.Any(character => char.IsControl(character) || character == '\\')))
            return Invalid("$.workflowId", "Use an exact team/type/name identity, with at most 128 Unicode characters per segment.");
        var allowed = await authorization.AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Type, identity[0] + "/" + identity[1], context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Operator));
        if (!allowed.Succeeded)
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "Forbidden", detail: "A triggering principal object ID is required.");
        if (!versions.IsReady) return Unavailable();
        Guid versionId;
        WorkflowManifest manifest;
        if (request.Version is null)
        {
            try
            {
                var selected = versions.GetActive(request.WorkflowId);
                (versionId, manifest) = (selected.VersionId, selected.Manifest);
            }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (InvalidOperationException) { return Unavailable(); }
        }
        else
        {
            var isId = Guid.TryParseExact(request.Version, "D", out var id);
            if (!isId)
            {
                try
                {
                    if (request.Version.Length > 256) throw new ArgumentException("Version is too long.");
                    _ = new ApiBaseline(request.Version, [], []);
                }
                catch (ArgumentException) { return Invalid("$.version", "Use an exact workflow version UUID or SemVer 2.0.0."); }
            }
            await using var query = source.CreateCommand($"""
                SELECT v.id,v.semver,v.manifest::text FROM workflow_versions v
                JOIN workflows w ON w.id=v.workflow_id
                WHERE w.id=$1 AND w.deleted_at IS NULL AND {(isId ? "v.id=$2" : "v.semver_key=split_part($2,'+',1)")}
                """);
            query.Parameters.AddWithValue(request.WorkflowId);
            if (isId) query.Parameters.AddWithValue(id);
            else query.Parameters.AddWithValue(request.Version);
            await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
            if (!await reader.ReadAsync(context.RequestAborted)) return NotFound();
            versionId = reader.GetGuid(0);
            manifest = JsonSerializer.Deserialize(reader.GetString(2), WorkflowManifestJsonContext.Default.WorkflowManifest)
                ?? throw new InvalidDataException("The selected workflow manifest is null.");
            if (manifest.Name != request.WorkflowId || manifest.Version != reader.GetString(1))
                throw new InvalidDataException("The selected workflow manifest does not match its stored identity.");
        }
        var variables = VariableBinder.Resolve(manifest.Variables, request.Variables, cancellationToken: context.RequestAborted);
        if (!variables.IsValid)
            return Results.Problem(statusCode: 400, title: "Run validation failed",
                extensions: new Dictionary<string, object?> { ["errors"] = variables.Errors });
        var now = time.GetUtcNow();
        var runId = Guid.CreateVersion7(now);
        var run = new RunState(runId, manifest.Nodes, manifest.Metadata, _ => { }, variables.Values);
        await inbox.TriggerAsync(new(run.Snapshot, request.WorkflowId, versionId, now.Add(manifest.RunTimeout)),
            actor.ToString("D"), context.RequestAborted);
        return Results.Json(new TriggerRunResponse(runId), RunJsonContext.Default.TriggerRunResponse, statusCode: 202);
    }

    private static IResult Invalid(string path, string hint) => Results.Problem(statusCode: 400,
        title: "Invalid run request", extensions: new Dictionary<string, object?>
        {
            ["errors"] = new[] { new ValidationError("RUN001", "request", path, "The run request does not match its contract.", hint) }
        });

    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "Run request exceeds its byte limit");
    private static IResult NotFound() => Results.Problem(statusCode: 404, title: "Workflow version not found");
    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Workflow versions are synchronizing");
}
