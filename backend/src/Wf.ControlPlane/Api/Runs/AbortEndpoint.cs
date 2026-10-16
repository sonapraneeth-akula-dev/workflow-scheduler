using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core.StateMachine;

namespace Wf.ControlPlane.Api;

public sealed record AbortRunRequest(string Reason, AbortSource Source = AbortSource.API);
public sealed record AbortRunResponse(string By, string ByName, DateTimeOffset At, string Reason, AbortSource Source)
{
    internal static AbortRunResponse From(AbortRecord record) =>
        new(record.By, record.ByName, record.At, record.Reason, record.Source);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AbortRunRequest))]
[JsonSerializable(typeof(AbortRunResponse))]
public partial class AbortRunJson : JsonSerializerContext;

public static class AbortEndpoint
{
    private const long MaximumRequestBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapRunAbortEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/runs/{id:guid}/abort", AcceptAsync)
            .RequireAuthorization()
            .Accepts<AbortRunRequest>("application/json")
            .Produces<AbortRunResponse>(202)
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415);
        return endpoints;
    }

    private static async Task<IResult> AcceptAsync(HttpContext context, Guid id, NpgsqlDataSource source, TimeProvider time)
    {
        context.Response.Headers.CacheControl = "no-store";
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Run, id.ToString("D"), context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Operator))).Succeeded)
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "An aborting principal object ID is required");
        if (!context.Request.HasJsonContentType())
            return Results.Problem(statusCode: 415, title: "A JSON request content type is required");
        if (context.Request.ContentLength > MaximumRequestBytes) return TooLarge();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaximumRequestBytes;
        AbortRunRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(AbortRunJson.Default.AbortRunRequest, context.RequestAborted);
        }
        catch (JsonException) { return Invalid(); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return TooLarge(); }
        if (request is null || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500 ||
            request.Source is not (AbortSource.UI or AbortSource.API or AbortSource.CLI))
            return Invalid();

        var token = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var state = await ReadRunAsync(connection, id, token);
        if (state is null)
        {
            // Recovery locks the trigger before creating its run. Follow that order for not-yet-materialized runs.
            await using var trigger = new NpgsqlCommand("""
                SELECT payload->>'WorkflowId' FROM commands WHERE run_id=$1 AND kind=0 FOR UPDATE
                """, connection, transaction);
            trigger.Parameters.AddWithValue(id);
            if (await trigger.ExecuteScalarAsync(token) is not string workflow)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            state = await ReadRunAsync(connection, id, token) ?? (RunStatus.Queued, workflow);
        }
        if (TransitionTable.IsTerminal(state.Value.Status))
            return Results.Problem(statusCode: 409, title: "The run is already terminal",
                extensions: new Dictionary<string, object?> { ["runStatus"] = state.Value.Status.ToString() });

        var by = actor.ToString("D");
        var name = context.User.FindFirstValue("name");
        var record = new AbortRecord(by, string.IsNullOrWhiteSpace(name) ? by : name, time.GetUtcNow(), request.Reason, request.Source);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO commands(id,kind,run_id,payload,created_by,created_at)
            VALUES($1,1,$2,$3::jsonb,$4,$5)
            ON CONFLICT(run_id,kind) DO NOTHING RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue(Guid.CreateVersion7());
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(JsonSerializer.Serialize(record, RecoveryJson.Default.AbortRecord));
        insert.Parameters.AddWithValue(by);
        insert.Parameters.AddWithValue(record.At);
        if (await insert.ExecuteScalarAsync(token) is Guid)
        {
            await using var audit = new NpgsqlCommand("""
                WITH logged AS (
                    INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,team_id,summary)
                    VALUES($1,$2,$3,'run.abort','Run',$4,$5,
                        jsonb_build_object('by',$2::text,'byName',$6::text,'at',$1::timestamptz,'reason',$7::text,'source',$8::text))
                    RETURNING target_id)
                SELECT pg_notify('commands',$4) FROM logged
                """, connection, transaction);
            audit.Parameters.AddWithValue(record.At);
            audit.Parameters.AddWithValue(by);
            audit.Parameters.AddWithValue(context.User.HasClaim("idtyp", "app") ? "App" : "User");
            audit.Parameters.AddWithValue(id.ToString("D"));
            audit.Parameters.AddWithValue(state.Value.Workflow[..state.Value.Workflow.IndexOf('/')]);
            audit.Parameters.AddWithValue(record.ByName);
            audit.Parameters.AddWithValue(record.Reason);
            audit.Parameters.AddWithValue(record.Source.ToString());
            await audit.ExecuteNonQueryAsync(token);
        }
        else
        {
            // Do not lock an existing abort command: recovery holds that lock while waiting for the run row.
            await using var original = new NpgsqlCommand("SELECT payload::text FROM commands WHERE run_id=$1 AND kind=1",
                connection, transaction);
            original.Parameters.AddWithValue(id);
            record = JsonSerializer.Deserialize((string)(await original.ExecuteScalarAsync(token)
                ?? throw new InvalidDataException("The accepted abort command is missing.")), RecoveryJson.Default.AbortRecord)
                ?? throw new InvalidDataException("The accepted abort record is null.");
        }
        await transaction.CommitAsync(token);
        return Results.Json(AbortRunResponse.From(record), AbortRunJson.Default.AbortRunResponse, statusCode: 202);
    }

    private static async Task<(RunStatus Status, string Workflow)?> ReadRunAsync(NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var query = new NpgsqlCommand("""
            SELECT status,workflow_id FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1 FOR UPDATE
            """, connection);
        query.Parameters.AddWithValue(id);
        await using var row = await query.ExecuteReaderAsync(token);
        return await row.ReadAsync(token) ? ((RunStatus)row.GetInt16(0), row.GetString(1)) : null;
    }

    private static IResult Invalid() => Results.Problem(statusCode: 400, title: "Invalid abort request",
        detail: "Supply a reason of 1-500 characters containing non-whitespace and source UI, API or CLI; no other properties are supported.");
    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "Abort request exceeds its byte limit");
}
