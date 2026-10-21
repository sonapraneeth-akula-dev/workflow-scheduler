using Npgsql;
using NpgsqlTypes;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Hosting;

public sealed record RuntimeInstance(string Id, string Name, string HostName, string NodeName, string? PodName,
    string? Namespace, bool EngineEnabled, string Version)
{
    public static RuntimeInstance Read(IConfiguration configuration, bool engineEnabled)
    {
        var host = Label(configuration["Runtime:HostName"] ?? Environment.MachineName, "Runtime:HostName");
        return new(Guid.NewGuid().ToString("N"),
            Label(configuration["Runtime:Name"] ?? $"kardia-{(engineEnabled ? "controller" : "api")}-{host}", "Runtime:Name"),
            host, Label(configuration["Runtime:NodeName"] ?? configuration["NODE_NAME"] ?? host, "Runtime:NodeName"),
            Optional(configuration["Runtime:PodName"] ?? configuration["POD_NAME"], "Runtime:PodName"),
            Optional(configuration["Runtime:Namespace"] ?? configuration["POD_NAMESPACE"], "Runtime:Namespace"),
            engineEnabled, typeof(RuntimeInstance).Assembly.GetName().Version!.ToString());
    }

    private static string? Optional(string? value, string key) => value is null ? null : Label(value, key);

    private static string Label(string value, string key) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl) || value != value.Trim()
            ? throw new InvalidOperationException($"{key} must contain 1..200 characters without surrounding whitespace or control characters.")
            : value;
}

public sealed class RuntimeActivity
{
    private long httpRequests;
    public long HttpRequests => Interlocked.Read(ref httpRequests);
    public void RequestHandled() => Interlocked.Increment(ref httpRequests);
}

public sealed class RuntimeInstanceReporter(NpgsqlDataSource source, RuntimeInstance instance, RuntimeActivity activity,
    LiveSubscriptions live, DrainService drain, TimeProvider time, ILogger<RuntimeInstanceReporter> logger) : BackgroundService
{
    public static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

    public async Task ReportAsync(CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO runtime_instances(id,name,host_name,node_name,pod_name,namespace,engine_enabled,version,
                ready,http_requests,live_connections,live_subscriptions,live_deliveries)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
            ON CONFLICT(id) DO UPDATE SET last_heartbeat_at=clock_timestamp(),ready=EXCLUDED.ready,
                http_requests=EXCLUDED.http_requests,live_connections=EXCLUDED.live_connections,
                live_subscriptions=EXCLUDED.live_subscriptions,live_deliveries=EXCLUDED.live_deliveries
            """);
        command.Parameters.AddWithValue(instance.Id);
        command.Parameters.AddWithValue(instance.Name);
        command.Parameters.AddWithValue(instance.HostName);
        command.Parameters.AddWithValue(instance.NodeName);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)instance.PodName ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)instance.Namespace ?? DBNull.Value);
        command.Parameters.AddWithValue(instance.EngineEnabled);
        command.Parameters.AddWithValue(instance.Version);
        command.Parameters.AddWithValue(drain.IsReady);
        command.Parameters.AddWithValue(activity.HttpRequests);
        command.Parameters.AddWithValue(live.ConnectionCount);
        command.Parameters.AddWithValue(live.SubscriptionCount);
        command.Parameters.AddWithValue(live.DeliveryCount);
        await command.ExecuteNonQueryAsync(token);
        await using var cleanup = source.CreateCommand(
            "DELETE FROM runtime_instances WHERE last_heartbeat_at<clock_timestamp()-interval '1 day'");
        await cleanup.ExecuteNonQueryAsync(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReportAsync(stoppingToken); }
                catch (Exception error) when (error is NpgsqlException or TimeoutException or IOException)
                { logger.LogError(error, "Runtime instance heartbeat unavailable; inventory will report this instance stale."); }
                await Task.Delay(HeartbeatPeriod, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await using var command = source.CreateCommand("""
                UPDATE runtime_instances SET ready=false,stopped_at=clock_timestamp(),last_heartbeat_at=clock_timestamp(),
                    live_connections=0,live_subscriptions=0 WHERE id=$1
                """);
            command.Parameters.AddWithValue(instance.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or IOException)
        { logger.LogError(error, "Cannot mark runtime instance stopped; heartbeat expiry remains the inventory authority."); }
    }
}

public static class RuntimeInstanceHosting
{
    public static IApplicationBuilder UseRuntimeActivity(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1") &&
            !context.Request.Path.StartsWithSegments(LiveHub.Path))
        {
            await next(context);
            return;
        }
        var instance = context.RequestServices.GetRequiredService<RuntimeInstance>();
        var activity = context.RequestServices.GetRequiredService<RuntimeActivity>();
        context.Response.Headers["X-Kardia-Instance"] = instance.Id;
        try { await next(context); }
        finally { activity.RequestHandled(); }
    });
}
