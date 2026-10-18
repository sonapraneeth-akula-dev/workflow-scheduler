using System.Diagnostics.Metrics;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Wf.Contracts;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core.Metrics;

namespace Wf.ControlPlane.Hosting;

public static class WorkflowTelemetryHosting
{
    public static IServiceCollection AddWorkflowTelemetry(this IServiceCollection services,
        IConfiguration configuration, bool engineEnabled, string environment)
    {
        if (!configuration.GetValue("Telemetry:Enabled", true)) return services;
        var endpoint = configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"];
        if (endpoint is null) return services;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT must be an HTTP(S) metrics receiver URL without credentials, query or fragment.");
        var interval = configuration.GetValue("OTEL_METRIC_EXPORT_INTERVAL", 15000);
        if (interval <= 0) throw new InvalidOperationException("OTEL_METRIC_EXPORT_INTERVAL must be positive.");
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("wf-control-plane", serviceInstanceId: Guid.NewGuid().ToString("N"))
                .AddAttributes([new("deployment.environment.name", environment)]))
            .WithMetrics(metrics => metrics.AddMeter(WorkflowTelemetry.MeterName)
                .AddView("wf.task.memory.peak", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [1048576, 8388608, 16777216, 33554432, 67108864, 134217728, 268435456,
                        536870912, 1073741824, 2147483648, 4294967296, 8589934592, 17179869184, 34359738368]
                })
                .AddView(instrument => instrument is Histogram<double> && instrument.Unit == "s"
                    ? new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.001, 0.01, 0.1, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600, 21600, 86400]
                    } : null)
                .AddOtlpExporter((exporter, reader) =>
                {
                    exporter.Endpoint = uri;
                    exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval;
                }));
        if (engineEnabled) services.AddHostedService<ActiveRunTelemetry>();
        return services;
    }
}

internal sealed class ActiveRunTelemetry(NpgsqlDataSource source, LeaseManager leadership, TimeProvider time,
    ILogger<ActiveRunTelemetry> logger) : BackgroundService
{
    private readonly Meter meter = new(WorkflowTelemetry.MeterName);
    private Measurement<long>[] values = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        meter.CreateObservableGauge("wf.run.active", () => Volatile.Read(ref values), "{run}",
            "Current durable queued, running and aborting runs; sampled by the leader.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (leadership.Current is null)
                        Volatile.Write(ref values, Volatile.Read(ref values)
                            .Select(value => new Measurement<long>(0, value.Tags.ToArray())).ToArray());
                    else
                    {
                        await using var query = source.CreateCommand("""
                            SELECT w.id,s.status,count(r.id) FROM workflows w
                            CROSS JOIN (VALUES (0),(1),(2)) s(status)
                            LEFT JOIN runs r ON r.workflow_id=w.id AND r.status=s.status
                            GROUP BY w.id,s.status ORDER BY w.id,s.status
                            """);
                        await using var reader = await query.ExecuteReaderAsync(stoppingToken);
                        var current = new List<Measurement<long>>();
                        while (await reader.ReadAsync(stoppingToken))
                        {
                            var identity = WorkflowMetricIdentity.FromWorkflowId(reader.GetString(0), "");
                            var tags = WorkflowTelemetry.Tags(identity);
                            tags.Add("status", ((RunStatus)reader.GetInt32(1)).ToString());
                            current.Add(new(reader.GetInt64(2), tags));
                        }
                        Volatile.Write(ref values, current.ToArray());
                    }
                }
                catch (NpgsqlException error)
                {
                    Volatile.Write(ref values, []);
                    logger.LogError(error, "Cannot sample durable active-run telemetry.");
                }
                await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        meter.Dispose();
        base.Dispose();
    }
}
