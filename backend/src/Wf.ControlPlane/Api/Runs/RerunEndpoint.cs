using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.Engine.Core.Rerun;
using Wf.Engine.Core.StateMachine;
using Wf.Validation;

namespace Wf.ControlPlane.Api;

internal sealed record AuthorizedRerun(Guid ParentRunId, RerunRunRequest Request, Guid Actor);

public static class RerunEndpoint
{
    private const long MaximumRequestBytes = 64 * 1024;

    internal static void MapRerunEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/runs/{id}/placement-options", PlacementOptions).RequireAuthorization()
            .Accepts<RerunRunRequest>("application/json").Produces<PlacementOptionsResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .AddEndpointFilter(Authorize);
        endpoints.MapPost("/api/v1/runs/{id}/rerun", Accept).RequireAuthorization()
            .Accepts<RerunRunRequest>("application/json").Produces<TriggerRunResponse>(202)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Fresh Run Operator authorization reruns a terminal run's exact pinned workflow version, " +
                "effective variables and metadata. Optional fromNodes invalidates those nodes and their descendants; " +
                "otherwise unsuccessful nodes rerun, or all nodes for a successful source. Reusable successful node " +
                "outputs and source lineage are copied into one durable leader-fenced rerun command. " +
                "Idempotency-Key replays the accepted run ID for 24 hours, scoped to actor and canonical request.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key", In = ParameterLocation.Header,
                    Description = "Optional retry key: one value of 1 to 200 visible ASCII characters without whitespace.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 200 }
                });
                return Task.CompletedTask;
            })
            .AddEndpointFilter(Authorize)
            .AddEndpointFilter<IdempotencyFilter>();
    }

    private static async ValueTask<object?> Authorize(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.RouteValues["id"] is not string id ||
            !Guid.TryParseExact(id, "D", out var parent) || parent.Version != 7 || parent.ToString("D") != id ||
            context.Request.QueryString.HasValue ||
            context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] is not { } path ||
            path != $"/api/v1/runs/{id}/rerun" && path != $"/api/v1/runs/{id}/placement-options")
            return Invalid("$.id", "Use one exact canonical UUIDv7 run identity with no query parameters.");
        if (!(await context.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Operator))).Succeeded)
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "A rerunning principal object ID is required");
        if (!context.Request.HasJsonContentType())
            return Results.Problem(statusCode: 415, title: "A JSON request content type is required");
        if (context.Request.ContentLength > MaximumRequestBytes) return TooLarge();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaximumRequestBytes;
        RerunRunRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(RunJsonContext.Default.RerunRunRequest, context.RequestAborted);
        }
        catch (JsonException) { return Invalid("$", "Supply an object with only optional fromNodes and placement."); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
        if (request is null) return Invalid("$", "Supply a rerun request object.");
        if (PlacementOverrideValidator.Invalid(request.Placement) is { } invalidPlacement)
            return Invalid("$.placement", invalidPlacement);
        if (request.FromNodes is { } nodes)
        {
            if (nodes.IsDefaultOrEmpty || nodes.Length > 10_000 ||
                nodes.Any(node => string.IsNullOrWhiteSpace(node) || node.Length > 128 || node.Any(char.IsControl)) ||
                nodes.Distinct(StringComparer.Ordinal).Count() != nodes.Length)
                return Invalid("$.fromNodes", "Specify nonempty unique declared node keys, or omit fromNodes.");
            request = request with { FromNodes = nodes.Order(StringComparer.Ordinal).ToImmutableArray() };
        }
        context.Features.Set(new AuthorizedRerun(parent, request, actor));
        context.Features.Set(new IdempotentRunRequest(actor, JsonSerializer.SerializeToElement(
            new RerunFingerprint(parent, request.FromNodes, request.Placement), RunJsonContext.Default.RerunFingerprint)));
        return await next(invocation);
    }

    private static Task<IResult> Accept(HttpContext context, string id, NpgsqlDataSource source, CommandInbox inbox,
        TimeProvider time, ILoggerFactory logs) => Handle(context, id, source, inbox, time, logs, false);

    private static Task<IResult> PlacementOptions(HttpContext context, string id, NpgsqlDataSource source, CommandInbox inbox,
        TimeProvider time, ILoggerFactory logs) => Handle(context, id, source, inbox, time, logs, true);

    private static async Task<IResult> Handle(HttpContext context, string id, NpgsqlDataSource source, CommandInbox inbox,
        TimeProvider time, ILoggerFactory logs, bool optionsOnly)
    {
        var authorized = context.Features.Get<AuthorizedRerun>()
            ?? throw new InvalidOperationException("Rerun acceptance requires authorization.");
        if (id != authorized.ParentRunId.ToString("D"))
            throw new InvalidOperationException("The authorized rerun source changed.");
        var token = context.RequestAborted;
        try
        {
            var shared = context.Features.Get<NpgsqlTransaction>();
            await using var ownedConnection = shared is null ? await source.OpenConnectionAsync(token) : null;
            var connection = shared?.Connection ?? ownedConnection
                ?? throw new InvalidOperationException("Rerun acceptance requires an open transaction.");
            await using var ownedTransaction = shared is null ? await connection.BeginTransactionAsync(token) : null;
            var transaction = shared ?? ownedTransaction!;
            string workflow;
            Guid version;
            Guid? series;
            await using (var header = new NpgsqlCommand("""
                SELECT workflow_id,version_id,series_id,status FROM runs
                WHERE run_day=uuid_v7_run_day($1) AND id=$1 FOR SHARE
                """, connection, transaction))
            {
                header.Parameters.AddWithValue(authorized.ParentRunId);
                await using var row = await header.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token))
                    return Results.Problem(statusCode: 409, title: "Only materialized terminal runs can be rerun");
                if (!Enum.IsDefined((RunStatus)row.GetInt16(3))) throw new InvalidDataException("Invalid source run status.");
                if (!TransitionTable.IsTerminal((RunStatus)row.GetInt16(3)))
                    return Results.Problem(statusCode: 409, title: "Only terminal runs can be rerun");
                workflow = row.GetString(0);
                version = row.GetGuid(1);
                series = row.IsDBNull(2) ? null : row.GetGuid(2);
            }
            var selected = await WorkflowVersionResolver.ResolveAsync(connection, workflow, version, token);
            if (authorized.Request.FromNodes is { } selectedNodes &&
                selectedNodes.Any(key => !selected.Manifest.Nodes.Any(node => node.Key == key)))
                return Invalid("$.fromNodes", "Use only node keys from this run's pinned workflow definition.");
            var store = new RunStore(source, logs.CreateLogger<RunStore>());
            var snapshot = await CommittedRunRead.Load(connection, store, authorized.ParentRunId, token);
            var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            await using (var read = new NpgsqlCommand("""
                SELECT node_key,COALESCE(outputs,'{}'::jsonb)::text FROM node_runs
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                """, connection, transaction))
            {
                read.Parameters.AddWithValue(authorized.ParentRunId);
                await using var rows = await read.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token))
                {
                    using var json = JsonDocument.Parse(rows.GetString(1));
                    outputs.Add(rows.GetString(0), json.RootElement.Clone());
                }
            }
            var now = time.GetUtcNow();
            var runId = Guid.CreateVersion7(now);
            var plan = RerunPlanner.Plan(runId, new(version, snapshot, outputs), selected.Manifest.Nodes,
                authorized.Request.FromNodes is { } fromNodes ? fromNodes : null);
            var variables = VariableBinder.Bind(selected.Manifest.Variables, snapshot.Variables);
            if (optionsOnly)
                return await PlacementOverrideValidator.OptionsAsync(connection, transaction, selected.Manifest,
                    variables, now, token);
            var selectors = await MachineSelectorInventory.ValidateAsync(connection, transaction,
                selected.Manifest with { Nodes = selected.Manifest.Nodes.Where(node =>
                    plan.Snapshot.Nodes[node.Key] == ExecutionStatus.Pending).ToImmutableArray() }, variables, token);
            if (!selectors.IsEmpty)
                return Results.Problem(statusCode: 400, title: "Run selector validation failed",
                    extensions: new Dictionary<string, object?> { ["errors"] = selectors });
            var placement = authorized.Request.Placement ?? snapshot.Placement;
            var placementErrors = await PlacementOverrideValidator.ValidateAsync(connection, transaction,
                selected.Manifest, variables, placement, now, token);
            if (!placementErrors.IsEmpty)
                return Results.Problem(statusCode: 400, title: "Run placement validation failed",
                    extensions: new Dictionary<string, object?> { ["errors"] = placementErrors });
            var command = new TriggerRunCommand(plan.Snapshot with { Placement = placement, PlacementAgentId = null },
                workflow, version, now.Add(selected.Manifest.RunTimeout),
                series, plan.ParentRunId, plan.ReusedNodes);
            await inbox.RerunAsync(command, authorized.Actor.ToString("D"), transaction, token);
            await using (var audit = new NpgsqlCommand("""
                INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,team_id,summary)
                VALUES($1,$2,$3,'run.rerun','Run',$4,$5,
                    jsonb_build_object('parentRunId',$6::uuid,'versionId',$7::uuid,'fromNodes',($8::jsonb)->'fromNodes'))
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue(now);
                audit.Parameters.AddWithValue(authorized.Actor.ToString("D"));
                audit.Parameters.AddWithValue(context.User.HasClaim("idtyp", "app") ? "App" : "User");
                audit.Parameters.AddWithValue(runId.ToString("D"));
                audit.Parameters.AddWithValue(workflow[..workflow.IndexOf('/')]);
                audit.Parameters.AddWithValue(plan.ParentRunId);
                audit.Parameters.AddWithValue(version);
                audit.Parameters.AddWithValue(JsonSerializer.Serialize(authorized.Request, RunJsonContext.Default.RerunRunRequest));
                await audit.ExecuteNonQueryAsync(token);
            }
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(token);
            return Results.Json(new TriggerRunResponse(runId), RunJsonContext.Default.TriggerRunResponse, statusCode: 202);
        }
        catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "Pinned workflow version not found"); }
        catch (NpgsqlException error)
        {
            logs.CreateLogger("RerunEndpoint").LogError("Rerun storage is unavailable ({SqlState}).", error.SqlState);
            return Results.Problem(statusCode: 503, title: "Rerun data is unavailable");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            logs.CreateLogger("RerunEndpoint").LogError("Rerun source data is inconsistent.");
            return Results.Problem(statusCode: 500, title: "Rerun source data is inconsistent");
        }
    }

    private static IResult Invalid(string path, string hint) => RunsEndpoints.Invalid(path, hint);
    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "Rerun request exceeds its byte limit");
}
