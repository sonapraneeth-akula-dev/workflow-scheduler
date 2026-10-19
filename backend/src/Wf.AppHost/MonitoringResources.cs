using Aspire.Hosting.ApplicationModel;

namespace Wf.AppHost;

internal static class MonitoringResources
{
    public static void Add(IDistributedApplicationBuilder builder, bool native, int prometheusPort = 9090, int grafanaPort = 3000)
    {
        var configuration = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "infra", "monitoring"));
        var prometheus = builder.AddContainer("wf-prometheus", "prom/prometheus", "v3.15.0")
            .WithBindMount(Path.Combine(configuration, "prometheus.yml"), "/etc/prometheus/prometheus.yml", isReadOnly: true)
            .WithVolume(native ? "wf-prometheus-data" : "prometheus-data", "/prometheus")
            .WithArgs("--config.file=/etc/prometheus/prometheus.yml", "--storage.tsdb.path=/prometheus",
                "--storage.tsdb.retention.time=30d", "--web.enable-otlp-receiver");
        var grafana = builder.AddContainer("wf-grafana", "grafana/grafana", "13.2.4")
            .WithBindMount(Path.Combine(configuration, "grafana", "provisioning"), "/etc/grafana/provisioning", isReadOnly: true)
            .WithBindMount(Path.Combine(configuration, "grafana", "dashboards"), "/var/lib/grafana/dashboards", isReadOnly: true)
            .WithVolume(native ? "wf-grafana-data" : "grafana-data", "/var/lib/grafana")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Viewer")
            .WithEnvironment("GF_AUTH_DISABLE_LOGIN_FORM", "true")
            .WithEnvironment("GF_AUTH_BASIC_ENABLED", "false")
            .WaitFor(prometheus);
        if (native)
        {
            prometheus.WithContainerName("kardia-local-prometheus-" + prometheusPort)
                .WithContainerNetworkAlias("wf-prometheus")
                .WithContainerRuntimeArgs("--publish", $"127.0.0.1:{prometheusPort}:9090")
                .WithUrl("http://127.0.0.1:" + prometheusPort, "Prometheus");
            grafana.WithContainerName("kardia-local-grafana-" + grafanaPort)
                .WithContainerRuntimeArgs("--publish", $"127.0.0.1:{grafanaPort}:3000")
                .WithUrl("http://127.0.0.1:" + grafanaPort + "/d/workflow-runs", "Workflow trends");
        }
    }
}
