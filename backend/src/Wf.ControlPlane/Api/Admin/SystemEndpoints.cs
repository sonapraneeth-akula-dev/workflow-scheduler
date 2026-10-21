using System.Data;
using System.Text.Json.Serialization;
using Npgsql;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Admin;

public sealed record SystemInstanceResponse(string Id, string Name, string HostName, string NodeName, string? PodName,
    string? Namespace, string Role, string Version, string Status, bool IsLeader, DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt, long HttpRequests, int LiveConnections, int LiveSubscriptions, long LiveDeliveries);
public sealed record SystemWorkerResponse(Guid Id, string Name, string Pool, string Status, DateTimeOffset LastHeartbeatAt,
    int MaxTasks, int SlotsUsed, long AssignedTasks, long RunningTasks, long ActiveDagNodes, long ActiveRuns,
    long ActiveWorkflows);
public sealed record SystemTotalsResponse(long QueuedRuns, long RunningRuns, long AbortingRuns, long ActiveWorkflows,
    long AssignedTasks, long RunningTasks, long ActiveDagNodes);
public sealed record SystemSnapshotResponse(DateTimeOffset ObservedAt, int HeartbeatTimeoutSeconds,
    IReadOnlyList<SystemInstanceResponse> Instances, IReadOnlyList<SystemWorkerResponse> Workers, SystemTotalsResponse Totals);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SystemSnapshotResponse))]
public partial class SystemJsonContext : JsonSerializerContext;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/admin/system", Read).RequireAuthorization(AuthExtensions.Admin)
            .Produces<SystemSnapshotResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(503)
            .WithDescription("Global Admin only. A database-time snapshot of observed API/controller processes, lease leadership, " +
                "workers and durable active work. Runtime heartbeats expire after 15 seconds; stopped/stale instances remain " +
                "visible for up to 24 hours. Counters are cumulative per process, not packet tracing. Assigned tasks include " +
                "Dispatched and Running attempts even on an unhealthy worker; counts do not imply that a process is alive. " +
                "No desired Kubernetes replicas or unobserved frontend pods are fabricated.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, NpgsqlDataSource source, ILoggerFactory logging)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "System inventory does not accept query parameters");
        try
        {
            var token = context.RequestAborted;
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await using var command = new NpgsqlCommand("""
                SELECT transaction_timestamp();
                SELECT i.id,i.name,i.host_name,i.node_name,i.pod_name,i.namespace,i.engine_enabled,i.version,
                    CASE WHEN i.stopped_at IS NOT NULL THEN 'Stopped'
                        WHEN i.last_heartbeat_at<=transaction_timestamp()-interval '15 seconds' THEN 'Unhealthy'
                        WHEN NOT i.ready THEN 'Draining' ELSE 'Healthy' END,
                    i.engine_enabled AND i.ready AND i.stopped_at IS NULL
                        AND i.last_heartbeat_at>transaction_timestamp()-interval '15 seconds'
                        AND EXISTS(SELECT 1 FROM leases l WHERE l.name='engine' AND l.holder=i.id
                            AND l.expires_at>transaction_timestamp()),
                    i.started_at,i.last_heartbeat_at,i.http_requests,i.live_connections,i.live_subscriptions,i.live_deliveries
                FROM runtime_instances i
                WHERE i.last_heartbeat_at>=transaction_timestamp()-interval '1 day'
                ORDER BY i.name COLLATE "C",i.id;
                SELECT a.id,a.name,a.pool,
                    CASE WHEN a.status=2 THEN 'Draining'
                        WHEN a.status=1 OR a.last_heartbeat_at<=transaction_timestamp()-interval '15 seconds' THEN 'Unhealthy'
                        ELSE 'Healthy' END,
                    a.last_heartbeat_at,a.max_tasks,a.slots_used,
                    count(t.run_id),count(t.run_id) FILTER(WHERE t.status=3),
                    count(DISTINCT (t.run_day,t.run_id,regexp_replace(t.task_key,'/[^/]*$',''))) FILTER(WHERE t.run_id IS NOT NULL),
                    count(DISTINCT t.run_id),count(DISTINCT r.workflow_id)
                FROM agents a LEFT JOIN task_attempts t ON t.agent_id=a.id AND t.status IN (2,3)
                LEFT JOIN runs r ON r.run_day=t.run_day AND r.id=t.run_id
                GROUP BY a.id ORDER BY a.name COLLATE "C",a.id;
                SELECT (SELECT count(*) FROM runs WHERE status=0),
                    (SELECT count(*) FROM runs WHERE status=1),
                    (SELECT count(*) FROM runs WHERE status=2),
                    (SELECT count(DISTINCT workflow_id) FROM runs WHERE status IN (0,1,2)),
                    count(*),count(*) FILTER(WHERE status=3),
                    count(DISTINCT (run_day,run_id,regexp_replace(task_key,'/[^/]*$','')))
                FROM task_attempts WHERE agent_id IS NOT NULL AND status IN (2,3);
                """, connection, transaction);
            var instances = new List<SystemInstanceResponse>();
            var workers = new List<SystemWorkerResponse>();
            SystemTotalsResponse totals;
            DateTimeOffset observedAt;
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                await reader.ReadAsync(token);
                observedAt = reader.GetFieldValue<DateTimeOffset>(0);
                await reader.NextResultAsync(token);
                while (await reader.ReadAsync(token))
                    instances.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetBoolean(6) ? "ApiController" : "Api", reader.GetString(7), reader.GetString(8),
                        reader.GetBoolean(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11),
                        reader.GetInt64(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt64(15)));
                await reader.NextResultAsync(token);
                while (await reader.ReadAsync(token))
                    workers.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetFieldValue<DateTimeOffset>(4), reader.GetInt32(5), reader.GetInt32(6),
                        reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11)));
                await reader.NextResultAsync(token);
                await reader.ReadAsync(token);
                totals = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
            }
            await transaction.CommitAsync(token);
            return Results.Json(new SystemSnapshotResponse(observedAt, 15, instances, workers, totals),
                SystemJsonContext.Default.SystemSnapshotResponse);
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or IOException)
        {
            logging.CreateLogger("Wf.SystemInventory").LogError(error, "Cannot read durable runtime inventory.");
            return Results.Problem(statusCode: 503, title: "System inventory unavailable");
        }
    }
}
