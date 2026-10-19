using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Live;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Api;

public static partial class RunEventsEndpoints
{
    private static readonly RunStoreJson DurableJson = new(new JsonSerializerOptions(RunStoreJson.Default.Options)
    {
        RespectRequiredConstructorParameters = true
    });
    public static IEndpointRouteBuilder MapRunEventsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapSystemEvents(endpoints);
        endpoints.MapGet("/api/v1/runs/{id}/events", Events).RequireAuthorization()
            .Produces<RunEventsPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(409).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Committed live resync after one canonical nonnegative Int64 afterSeq. " +
                "Returns at most 5000 ordered events and their authoritative lastSeq; 409 requires a new run snapshot.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "afterSeq", In = ParameterLocation.Query, Required = true,
                    Description = "Canonical nonnegative Int64 decimal (0–9223372036854775807); preserve exact digits.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^(0|[1-9][0-9]{0,18})$" }
                });
                return Task.CompletedTask;
            });
        endpoints.MapGet("/api/v1/runs/{id}", Snapshot).RequireAuthorization()
            .Produces<RunSnapshotResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Authoritative committed run state restored from its materialized snapshot and event tail. " +
                "An accepted pending trigger exposes its queued initial state at lastSeq 0 before materialization. " +
                "lastSeq matches the returned graph state. No variables, payloads, outputs, or storage capabilities.");
        return endpoints;
    }

    private static Task<IResult> Events(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization) =>
        Read(context, id, source, authorization, snapshot: false);

    private static Task<IResult> Snapshot(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization) => Read(context, id, source, authorization, snapshot: true);

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, bool snapshot)
    {
        context.Response.Headers.CacheControl = "no-store";
        Guid run;
        try
        {
            run = RunEventsQuery.RunId(id);
            var rawPath = context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0];
            if (rawPath != $"/api/v1/runs/{id}" + (snapshot ? "" : "/events"))
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Problem(400, "Invalid run identity", "invalid_run_identity"); }
        var allowed = await authorization.AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
        if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        long after = 0;
        try
        {
            if (snapshot)
            {
                if (context.Request.QueryString.HasValue) throw new ArgumentException();
            }
            else
            {
                after = RunEventsQuery.AfterSequence(context.Request.Query);
                if (context.Request.QueryString.Value != "?afterSeq=" + after.ToString(CultureInfo.InvariantCulture))
                    throw new ArgumentException();
            }
        }
        catch (ArgumentException) { return Problem(400, "Invalid run query", "invalid_run_query"); }
        var token = context.RequestAborted;
        try
        {
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var header = await Header(connection, run, token) ?? await PendingTrigger(connection, run, token);
            if (header is null) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            if (!snapshot && after > header.LastSeq)
                return Problem(400, "afterSeq exceeds the committed run sequence", "after_seq_ahead");
            var result = snapshot ? await ReadSnapshot(connection, header, token) :
                await ReadEvents(connection, header, after, token);
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException)
        { return Problem(500, "Committed run data is inconsistent", "run_data_inconsistent"); }
        catch (NpgsqlException) { return Problem(503, "Committed run data is unavailable", "run_data_unavailable"); }
    }

    private static async Task<RunHeader?> Header(NpgsqlConnection connection, Guid run, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT workflow_id,version_id,series_id,status,last_seq,snapshot_seq,parent_run_id
            FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1
            """, connection);
        command.Parameters.AddWithValue(run);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var header = new RunHeader(run, reader.GetString(0), reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetInt16(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6));
        if (header.LastSeq < 0 || header.SnapshotSeq < 0 || header.SnapshotSeq > header.LastSeq ||
            !Enum.IsDefined((RunStatus)header.Status))
            throw new InvalidDataException();
        return header;
    }

    private static async Task<IResult> ReadEvents(NpgsqlConnection connection, RunHeader header, long after,
        CancellationToken token)
    {
        if (header.PendingState is not null) return ResyncResponse(header.RunId, after, header.LastSeq, []);
        await using var command = Tail(connection, header.RunId, after, RunEventsQuery.MaximumEvents + 1);
        await using var reader = await command.ExecuteReaderAsync(token);
        var events = new List<LiveEvent>();
        var sequence = after;
        while (await reader.ReadAsync(token))
        {
            var change = Event(reader, header.RunId, checked(sequence + 1));
            if (change.Sequence > header.LastSeq) throw new InvalidDataException();
            sequence = change.Sequence;
            events.Add(LiveEvent.From(change));
        }
        return ResyncResponse(header.RunId, after, header.LastSeq, events.ToArray());
    }

    public static IResult ResyncResponse(Guid runId, long afterSeq, long lastSeq, LiveEvent[] events)
    {
        if (afterSeq < 0 || lastSeq < afterSeq)
            return Problem(400, "afterSeq exceeds the committed run sequence", "after_seq_ahead");
        if (events.Length > RunEventsQuery.MaximumEvents + 1 ||
            events.Where((change, index) => index >= lastSeq - afterSeq || change.Seq != afterSeq + index + 1).Any())
            return Problem(500, "Committed run data is inconsistent", "run_data_inconsistent");
        if (events.Length > RunEventsQuery.MaximumEvents && RunEventsQuery.RequiresSnapshot(afterSeq, lastSeq))
            return Problem(409, "Run event resync exceeds 5000 events; reload the run snapshot", "resync_required");
        if (events.LongLength != lastSeq - afterSeq)
            return Problem(500, "Committed run data is inconsistent", "run_data_inconsistent");
        return Results.Json(new RunEventsPage(runId, lastSeq, events),
            RunEventsJsonContext.Default.RunEventsPage, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> ReadSnapshot(NpgsqlConnection connection, RunHeader header, CancellationToken token)
    {
        if (header.PendingState is { } pending) return SnapshotResponse(header, pending);
        await using var command = new NpgsqlCommand("""
            SELECT payload FROM run_snapshots
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND seq=$2
            """, connection);
        command.Parameters.AddWithValue(header.RunId);
        command.Parameters.AddWithValue(header.SnapshotSeq);
        var payload = await command.ExecuteScalarAsync(token) as byte[] ?? throw new InvalidDataException();
        var materialized = JsonSerializer.Deserialize(payload, DurableJson.RunSnapshot)
            ?? throw new InvalidDataException();
        if (materialized.RunId != header.RunId || materialized.LastSequence != header.SnapshotSeq)
            throw new InvalidDataException();
        var state = RunState.Restore(materialized, [], _ => { });
        await using (var tail = Tail(connection, header.RunId, header.SnapshotSeq))
        await using (var reader = await tail.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                state.Apply(Event(reader, header.RunId, checked(state.Snapshot.LastSequence + 1)));
        var reused = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var lineage = new NpgsqlCommand("""
            SELECT node_key,reused_from_run_id FROM node_runs
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND reused_from_run_id IS NOT NULL
            """, connection))
        {
            lineage.Parameters.AddWithValue(header.RunId);
            await using var reader = await lineage.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) reused.Add(reader.GetString(0), reader.GetGuid(1));
        }
        return SnapshotResponse(header with { ReusedNodes = reused }, state.Snapshot);
    }

    private static IResult SnapshotResponse(RunHeader header, RunSnapshot restored)
    {
        if (restored.LastSequence != header.LastSeq || (int)restored.Status != header.Status)
            throw new InvalidDataException();
        var reused = header.ReusedNodes ?? new Dictionary<string, Guid>();
        if (header.ParentRunId is { } parent && (parent == header.RunId || parent.Version != 7) ||
            reused.Count != restored.Nodes.Count(pair => pair.Value == ExecutionStatus.Reused) ||
            reused.Any(pair => header.ParentRunId is null || pair.Value == header.RunId ||
                pair.Value.Version != 7 ||
                !restored.Nodes.TryGetValue(pair.Key, out var status) || status != ExecutionStatus.Reused))
            throw new InvalidDataException();
        var abort = restored.Abort is not { } accepted ? null : new LiveAbort(accepted.By, accepted.ByName,
            accepted.At.ToString("O", CultureInfo.InvariantCulture), accepted.Reason, accepted.Source.ToString());
        return Results.Json(new RunSnapshotResponse(header.RunId, header.WorkflowId, header.VersionId, header.SeriesId,
            (int)restored.Status, restored.LastSequence,
            restored.Nodes.ToDictionary(pair => pair.Key, pair => (int)pair.Value),
            restored.Tasks.ToDictionary(pair => pair.Key, pair => (int)pair.Value), abort,
            restored.EndedAt?.ToString("O", CultureInfo.InvariantCulture), header.ParentRunId, reused)
            {
                Placement = restored.Placement, PlacementAgentId = restored.PlacementAgentId,
                TaskWaitReasons = restored.TaskWaitReasons,
                ConditionOutcomes = restored.ConditionOutcomes.ToDictionary(pair => pair.Key, pair => pair.Value.ToString())
            },
            RunEventsJsonContext.Default.RunSnapshotResponse);
    }

    private static async Task<RunHeader?> PendingTrigger(NpgsqlConnection connection, Guid run, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT payload::text,created_at FROM commands
            WHERE run_id=$1 AND kind=0 AND processed_at IS NULL
            """, connection);
        command.Parameters.AddWithValue(run);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var trigger = CommandInbox.ReadInitial(reader.GetString(0));
        if (trigger.InitialState.RunId != run || trigger.DeadlineAt <= reader.GetFieldValue<DateTimeOffset>(1) ||
            !WorkflowIdentity.TryGetTypeScope(trigger.WorkflowId, out _))
            throw new InvalidDataException();
        return new(run, trigger.WorkflowId, trigger.VersionId, trigger.SeriesId,
            (int)trigger.InitialState.Status, 0, 0, trigger.ParentRunId, trigger.InitialState,
            trigger.ReusedNodes?.ToDictionary(pair => pair.Key, pair => pair.Value.ReusedFromRunId, StringComparer.Ordinal));
    }

    private static NpgsqlCommand Tail(NpgsqlConnection connection, Guid run, long after, int? limit = null)
    {
        var command = new NpgsqlCommand("""
            SELECT seq,payload FROM run_events
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND seq>$2 ORDER BY seq
            """ + (limit is null ? "" : " LIMIT $3"), connection);
        command.Parameters.AddWithValue(run);
        command.Parameters.AddWithValue(after);
        if (limit is not null) command.Parameters.AddWithValue(limit.Value);
        return command;
    }

    private static RunEvent Event(NpgsqlDataReader reader, Guid run, long sequence)
    {
        var change = JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(1), DurableJson.RunEvent)
            ?? throw new InvalidDataException();
        if (reader.GetInt64(0) != sequence || change.Sequence != sequence || change.RunId != run ||
            change.At.Offset != TimeSpan.Zero ||
            change.Abort is { } abort && (abort.At > change.At || change.Entity != EventEntity.Run ||
                (RunStatus)change.State is not (RunStatus.Aborting or RunStatus.Aborted)) ||
            (change.Entity switch
            {
                EventEntity.Run => change.Key is not null || !Enum.IsDefined((RunStatus)change.State),
                EventEntity.Node or EventEntity.Task => string.IsNullOrWhiteSpace(change.Key) ||
                    !Enum.IsDefined((ExecutionStatus)change.State) || change.Abort is not null,
                _ => true
            }))
            throw new InvalidDataException();
        return change;
    }

    private static IResult Problem(int status, string title, string reason) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["reason"] = reason });

    private sealed record RunHeader(Guid RunId, string WorkflowId, Guid VersionId, Guid? SeriesId,
        int Status, long LastSeq, long SnapshotSeq, Guid? ParentRunId, RunSnapshot? PendingState = null,
        IReadOnlyDictionary<string, Guid>? ReusedNodes = null);
}
