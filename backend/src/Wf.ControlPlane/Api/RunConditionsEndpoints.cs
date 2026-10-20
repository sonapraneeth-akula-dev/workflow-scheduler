using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Api;

public sealed record RunConditionsResponse(Guid RunId, Guid VersionId, string LastSeq, ConditionDebugDetails[] Conditions);

public sealed record ConditionDebugDetails(string EntityKey, string? VariableKey, string? NodeKey, string? OutputKey,
    bool Expected, bool? Actual, string? Outcome, DateTimeOffset? EvaluatedAt, string? EvaluatedSequence,
    string SourceKind, bool? DeclaredDefault, Guid SourceRunId, string? ProducerTaskKey, int? ProducerAttempt,
    DateTimeOffset? ProducedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunConditionsResponse))]
public partial class RunConditionsJsonContext : JsonSerializerContext;

public static class RunConditionsEndpoints
{
    public static IEndpointRouteBuilder MapRunConditionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/conditions", Read).RequireAuthorization()
            .Produces<RunConditionsResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Pinned node/task Boolean conditions and durable evaluation evidence. " +
                "Fresh Run Viewer authorization; no query parameters. Actual is derived only from a recorded decision. " +
                "Only referenced Boolean defaults are returned, never arbitrary variables or output objects. " +
                "Producer identifies the successful task attempt that committed the referenced key before evaluation, " +
                "not arbitrary computation lineage. Null means unavailable/not recorded. Variable override/default " +
                "origin was not recorded; declaredDefault is not proof of how the run value was supplied.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, RunStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        Guid runId;
        try
        {
            runId = RunEventsQuery.RunId(id);
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/runs/{id}/conditions")
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Problem(400, "Invalid run identity", "invalid_run_identity"); }
        try
        {
            if (!(await authorization.AuthorizeAsync(context.User,
                new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded)
                return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            if (context.Request.QueryString.HasValue)
                return Problem(400, "Invalid run query", "invalid_run_query");
            var token = context.RequestAborted;
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var snapshot = await CommittedRunRead.Load(connection, store, runId, token);
            var (version, manifest) = await Definition(connection, runId, token);
            var decisions = await Decisions(connection, snapshot, token);
            var conditions = new List<ConditionDebugDetails>();
            foreach (var node in manifest.Nodes)
            {
                if (node.When is { } nodeCondition)
                    conditions.Add(await Details(connection, snapshot, manifest, node.Key, nodeCondition, decisions, token));
                foreach (var task in node.Tasks)
                    if (task.When is { } taskCondition)
                        conditions.Add(await Details(connection, snapshot, manifest, $"{node.Key}/{task.Key}",
                            taskCondition, decisions, token));
            }
            await transaction.CommitAsync(token);
            return Results.Json(new RunConditionsResponse(runId, version,
                snapshot.LastSequence.ToString(CultureInfo.InvariantCulture), conditions.ToArray()),
                RunConditionsJsonContext.Default.RunConditionsResponse);
        }
        catch (NpgsqlException) { return Problem(503, "Committed run data is unavailable", "run_data_unavailable"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException or KeyNotFoundException or NullReferenceException)
        { return Problem(500, "Committed run data is inconsistent", "run_data_inconsistent"); }
    }

    private static async Task<(Guid Version, WorkflowManifest Manifest)> Definition(
        NpgsqlConnection connection, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            WITH identity AS (
                SELECT workflow_id,version_id FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1
                UNION ALL
                SELECT payload->>'WorkflowId',(payload->>'VersionId')::uuid FROM commands
                WHERE run_id=$1 AND kind=0 AND processed_at IS NULL
                    AND NOT EXISTS(SELECT 1 FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1)
            )
            SELECT v.id,v.manifest::text FROM identity i
            JOIN workflow_versions v ON v.workflow_id=i.workflow_id AND v.id=i.version_id
            """, connection);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException();
        return (reader.GetGuid(0), JsonSerializer.Deserialize(reader.GetString(1),
            WorkflowManifestJsonContext.Default.WorkflowManifest) ?? throw new InvalidDataException());
    }

    private static async Task<Dictionary<string, RunEvent>> Decisions(
        NpgsqlConnection connection, RunSnapshot snapshot, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT payload FROM run_events
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND seq<=$2
                AND convert_from(payload,'UTF8')::jsonb ? 'ConditionOutcome'
            ORDER BY seq
            """, connection);
        command.Parameters.AddWithValue(snapshot.RunId);
        command.Parameters.AddWithValue(snapshot.LastSequence);
        var decisions = new Dictionary<string, RunEvent>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var decision = JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(0), RunStoreJson.Default.RunEvent)
                ?? throw new InvalidDataException();
            decision.ValidateCondition();
            if (decision.RunId != snapshot.RunId || decision.Key is null || decision.ConditionOutcome is null ||
                !snapshot.ConditionOutcomes.TryGetValue(decision.Key, out var outcome) ||
                outcome != decision.ConditionOutcome || !decisions.TryAdd(decision.Key, decision))
                throw new InvalidDataException();
        }
        return decisions;
    }

    private static async Task<ConditionDebugDetails> Details(NpgsqlConnection connection, RunSnapshot snapshot,
        WorkflowManifest manifest, string key, Condition condition, Dictionary<string, RunEvent> decisions,
        CancellationToken token)
    {
        var outcome = snapshot.ConditionOutcomes.TryGetValue(key, out var saved) ? saved : (ConditionOutcome?)null;
        var decision = decisions.GetValueOrDefault(key);
        bool? actual = outcome switch
        {
            ConditionOutcome.Matched => condition.Expected,
            ConditionOutcome.NotMatched => !condition.Expected,
            _ => null
        };
        bool? declaredDefault = null;
        if (condition.VariableKey is { } variable)
        {
            var schema = manifest.Variables.SingleOrDefault(v => v.Key == variable);
            if (schema?.Type != VariableType.Bool) throw new InvalidDataException();
            if (schema.Default is { ValueKind: JsonValueKind.True or JsonValueKind.False } value)
                declaredDefault = value.GetBoolean();
        }
        var details = new ConditionDebugDetails(key, condition.VariableKey, condition.NodeKey, condition.OutputKey,
            condition.Expected, actual, outcome?.ToString(), decision?.At,
            decision?.Sequence.ToString(CultureInfo.InvariantCulture),
            condition.VariableKey is null ? "NodeOutput" : "RunVariable", declaredDefault, snapshot.RunId, null, null, null);
        if (condition.VariableKey is not null || decision is null ||
            outcome is null or ConditionOutcome.MissingValue) return details;
        return await Producer(connection, details, decision.Sequence, token);
    }

    private static async Task<ConditionDebugDetails> Producer(NpgsqlConnection connection, ConditionDebugDetails details,
        long before, CancellationToken token)
    {
        var sourceRun = details.SourceRunId;
        await using (var reused = new NpgsqlCommand("""
            SELECT n.reused_from_run_id FROM node_runs n
            JOIN runs current ON current.run_day=n.run_day AND current.id=n.run_id
            JOIN runs source ON source.run_day=uuid_v7_run_day(n.reused_from_run_id)
                AND source.id=n.reused_from_run_id AND source.workflow_id=current.workflow_id
            WHERE n.run_day=uuid_v7_run_day($1) AND n.run_id=$1 AND n.node_key=$2 AND n.status=$3
            """, connection))
        {
            reused.Parameters.AddWithValue(sourceRun);
            reused.Parameters.AddWithValue(details.NodeKey!);
            reused.Parameters.AddWithValue((short)ExecutionStatus.Reused);
            if (await reused.ExecuteScalarAsync(token) is Guid original)
            {
                sourceRun = original;
                before = long.MaxValue;
                details = details with { SourceRunId = original };
            }
        }
        await using var command = new NpgsqlCommand("""
            SELECT a.task_key,a.attempt,e.ts,
                CASE WHEN jsonb_typeof(a.outputs::jsonb->$3)='boolean'
                    THEN (a.outputs::jsonb->>$3)::boolean END
            FROM run_events e JOIN task_attempts a
                ON a.run_day=e.run_day AND a.run_id=e.run_id AND a.task_key=e.entity_key AND a.attempt=e.attempt
            WHERE e.run_day=uuid_v7_run_day($1) AND e.run_id=$1 AND e.seq<$2 AND e.kind=$4
                AND split_part(a.task_key,'/',1)=$5 AND a.outputs::jsonb ? $3
                AND (convert_from(e.payload,'UTF8')::jsonb->>'State')::integer=$6
            ORDER BY e.seq DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue(sourceRun);
        command.Parameters.AddWithValue(before);
        command.Parameters.AddWithValue(details.OutputKey!);
        command.Parameters.AddWithValue((short)EventEntity.Task);
        command.Parameters.AddWithValue(details.NodeKey!);
        command.Parameters.AddWithValue((int)ExecutionStatus.Succeeded);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return details;
        if (details.Outcome == nameof(ConditionOutcome.InvalidType)
            ? !reader.IsDBNull(3)
            : reader.IsDBNull(3) || reader.GetBoolean(3) != details.Actual)
            throw new InvalidDataException();
        return details with { ProducerTaskKey = reader.GetString(0), ProducerAttempt = reader.GetInt16(1),
            ProducedAt = reader.GetFieldValue<DateTimeOffset>(2) };
    }

    private static IResult Problem(int status, string title, string reason) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
