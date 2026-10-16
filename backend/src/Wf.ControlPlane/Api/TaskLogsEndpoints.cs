using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Api;

public static class TaskLogsEndpoints
{
    public static IEndpointRouteBuilder MapTaskLogsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/runs/{id}/tasks/{task}/{attempt}/logs", Get)
            .RequireAuthorization()
            .Produces<TaskLogsPage>(200, "application/json", "application/x-ndjson")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Responses!["200"].Content!["application/x-ndjson"].Schema =
                    new OpenApiSchema { Type = JsonSchemaType.String };
                return Task.CompletedTask;
            })
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(422).ProducesProblem(502).ProducesProblem(504)
            .WithDescription("Viewer task-attempt logs. task is the URI-escaped stored node/task key. " +
                "offset is zero-based original record offset; length is 1–1000 (default 200). " +
                "Alternatively search (regex, 2048 characters, 10 s total, 100 ms per match, 1000 matches, cursor), " +
                "or download=true (NDJSON).");
        return endpoints;
    }

    private static async Task<IResult> Get(HttpContext context, string id, string task, string attempt,
        NpgsqlDataSource source, IAuthorizationService authorization, IBlobStore store,
        string? offset = null, string? length = null, string? search = null, string? download = null, string? cursor = null)
    {
        // Bind only strings at the trust boundary; query validation below rejects duplicate/unknown parameters.
        _ = (offset, length, search, download, cursor);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        Wf.Contracts.Agent.V1.AttemptRef identity;
        TaskLogsQuery query;
        var rawPath = context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0].Split('/');
        if (rawPath is not { Length: >= 5 } || rawPath[^5] != id || rawPath[^2] != attempt)
            return Problem(400, "Invalid log identity", "invalid_log_identity");
        try { identity = TaskLogsQuery.Identity(id, rawPath[^3], attempt); }
        catch (ArgumentException) { return Problem(400, "Invalid log identity", "invalid_log_identity"); }
        if (rawPath[^3] != Uri.EscapeDataString(identity.NodeKey + "/" + identity.TaskKey))
            return Problem(400, "Invalid log identity", "invalid_log_identity");
        var allowed = await authorization.AuthorizeAsync(context.User,
            new TeamResource(TeamResourceKind.Run, id, context.RequestAborted),
            TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
        if (!allowed.Succeeded) return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        await using var command = source.CreateCommand("""
            SELECT status FROM task_attempts
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND task_key=$2 AND attempt=$3
            """);
        command.Parameters.AddWithValue(Guid.Parse(id));
        command.Parameters.AddWithValue(identity.NodeKey + "/" + identity.TaskKey);
        command.Parameters.AddWithValue((short)identity.Attempt);
        if (await command.ExecuteScalarAsync(context.RequestAborted) is not short status)
            return Results.Problem(TeamAuthorizationExtensions.Forbidden());
        try { query = TaskLogsQuery.Parse(context.Request.Query); }
        catch (ArgumentException) { return Problem(400, "Invalid log query", "invalid_log_query"); }
        var reader = new TaskLogReader(store);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (query.Search is not null) budget.CancelAfter(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        try
        {
            Regex? regex = null;
            if (query.Search is not null)
            {
                try { regex = new Regex(query.Search, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { return Problem(400, "Invalid log search regular expression", "invalid_log_regex"); }
            }
            var index = await reader.ReadIndexAsync(identity, budget.Token);
            if (!index.Complete) return Problem(409, "Logs are still uploading", "log_not_ready");
            if (query.Download)
                return new LogDownloadResult(reader, index, $"{id}-{identity.Attempt}.ndjson");
            var records = new List<TaskLogRecord>();
            var matches = new List<TaskLogMatch>();
            string? next = null;
            await foreach (var record in reader.ReadAsync(index, regex is null ? query.Offset + 1 : query.AfterLine,
                regex is null ? query.Offset + query.Length : long.MaxValue, budget.Token))
            {
                if (record.Marker) continue;
                if (regex is null) { records.Add(new(record.Line, record.Frame)); continue; }
                if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new TimeoutException();
                var start = record.Line == query.AfterLine ? query.AfterIndex : 0;
                if (start > record.Frame.Text.Length) continue;
                var match = regex.Match(record.Frame.Text, start);
                while (match.Success)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new TimeoutException();
                    matches.Add(new(record.Line, match.Index, match.Length, record.Frame));
                    var position = match.Index + Math.Max(match.Length, 1);
                    if (matches.Count == 1000)
                    {
                        next = position <= record.Frame.Text.Length ? TaskLogsQuery.Cursor(record.Line, position) :
                            record.Line < index.Lines ? TaskLogsQuery.Cursor(record.Line + 1, 0) : null;
                        break;
                    }
                    match = match.NextMatch();
                }
                if (matches.Count == 1000) break;
            }
            return Results.Json(new TaskLogsPage(index.Lines, index.DroppedLines, index.Truncated, index.CaptureInterrupted,
                index.TruncationMarker, records, matches, next), TaskLogsJsonContext.Default.TaskLogsPage);
        }
        catch (RegexMatchTimeoutException) { return Problem(422, "Log search exceeded its execution budget", "log_search_timeout"); }
        catch (TimeoutException) { return Problem(422, "Log search exceeded its execution budget", "log_search_timeout"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return Problem(504, "Log search exceeded its ten-second total budget", "log_search_timeout"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return Problem(status is >= 0 and <= 4 ? 409 : 404, "Logs are unavailable or still uploading", "log_not_available"); }
        catch (Exception error) when (IsStorageError(error))
        { return Problem(502, "Log storage is unavailable or corrupt", "log_storage_error"); }
    }

    private static bool IsStorageError(Exception error) =>
        error is IOException or InvalidDataException or JsonException or RequestFailedException or NotSupportedException or
            OverflowException or UnauthorizedAccessException;

    private static IResult Problem(int status, string title, string reason) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["reason"] = reason });

    private sealed class LogDownloadResult(TaskLogReader reader, Wf.Contracts.Logs.LogIndex index, string name) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try
            {
                context.Response.ContentType = "application/x-ndjson; charset=utf-8";
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
                context.Response.Headers["X-Log-Capture-Interrupted"] = index.CaptureInterrupted ? "true" : "false";
                context.Response.Headers["X-Log-Truncated"] = index.Truncated ? "true" : "false";
                await foreach (var record in reader.ReadAsync(index, token: context.RequestAborted))
                {
                    await context.Response.Body.WriteAsync(record.Bytes, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (Exception error) when (IsStorageError(error))
            {
                if (context.Response.HasStarted) context.Abort();
                else
                {
                    context.Response.Headers.Remove("Content-Disposition");
                    context.Response.ContentType = null;
                    await Problem(502, "Log storage is unavailable or corrupt", "log_storage_error").ExecuteAsync(context);
                }
            }
        }
    }
}
