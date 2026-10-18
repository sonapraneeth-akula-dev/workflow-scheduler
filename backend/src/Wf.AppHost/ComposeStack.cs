using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Wf.AppHost;

public static class ComposeStack
{
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        if (builder.ExecutionContext.IsRunMode)
            throw new InvalidOperationException("The local Compose profile requires publish mode.");
        var environment = builder.Configuration["Local:ComposeEnvironment"] ?? "ppe";
        if (environment is not ("dev" or "ppe"))
            throw new InvalidOperationException("Local:ComposeEnvironment must be dev or ppe.");
        if (builder.Configuration.GetValue("Local:Lan", false) ||
            builder.Configuration.GetConnectionString("wf") is not null)
            throw new InvalidOperationException("Compose owns its isolated database and network; native database/LAN overrides are not supported.");
        builder.AddDockerComposeEnvironment("compose")
            .WithProperties(resource => resource.DefaultNetworkName = "default")
            .WithDashboard(dashboard => dashboard.WithHostPort(18888)
                .WithEnvironment("DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS", "true"))
            .ConfigureComposeFile(file =>
            {
                file.Name = "kardia-" + environment;
                foreach (var service in file.Services.Values) service.Ports.Clear();
            });
        var password = builder.AddParameter("postgres-password", secret: true);
        MonitoringResources.Add(builder, native: false);
        var key = builder.AddParameter("storage-key", secret: true);
        var id = builder.AddParameter("agent-id");
        var postgres = builder.AddPostgres("postgres", password: password).WithImageTag("17-alpine")
            .WithEnvironment("POSTGRES_DB", "workflow").WithDataVolume("postgres-data");
        var database = postgres.AddDatabase("wf", "workflow");
        var migrations = builder.AddContainer("wf-migrations", "kardia-control-plane", environment)
            .WithReference(database, connectionName: "Workflow").WithArgs("--migrate").WaitFor(postgres);
        var api = AddHost("wf-api", engine: false);
        var first = AddHost("wf-engine-a", engine: true);
        var second = AddHost("wf-engine-b", engine: true);
        var gateway = builder.AddContainer("wf-gateway", "kardia-gateway", environment)
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WaitFor(api).WaitFor(first).WaitFor(second);
        builder.AddContainer("wf-agent-linux", "kardia-agent", environment)
            .WithEnvironment("Agent__Id", id)
            .WithEnvironment("Agent__Name", "compose-" + environment + "-linux")
            .WithEnvironment("Agent__Pool", "default")
            .WithEnvironment("Agent__Labels__environment", environment)
            .WithEnvironment("Agent__Labels__runtime", "container")
            .WithEnvironment("Agent__AllowInsecureTransport", "true")
            .WithEnvironment("Agent__InsecurePackageOrigin", "http://wf-api:8080")
            .WithEnvironment("Agent__Engines__0", gateway.GetEndpoint("http"))
            .WithEnvironment("Agent__ResultJournalDirectory", "/var/lib/wf-agent/results")
            .WithEnvironment("Agent__LogSpoolDirectory", "/var/lib/wf-agent/logs")
            .WithEnvironment("Agent__PackageCache", "/var/lib/wf-agent/packages")
            .WithEnvironment("Secrets__Provider", "Local")
            .WithEnvironment("Secrets__LocalDirectory", "/var/lib/wf-agent/secrets")
            .WithVolume("agent-data", "/var/lib/wf-agent").WaitFor(gateway);

        IResourceBuilder<ContainerResource> AddHost(string name, bool engine) =>
            builder.AddContainer(name, "kardia-control-plane", environment)
                .WithReference(database, connectionName: "Workflow")
                .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                .WithEnvironment("Authentication__Mode", "Development")
                .WithEnvironment("LocalCompose__Enabled", "true")
                .WithEnvironment("LocalCompose__Environment", environment)
                .WithEnvironment("WF_WORKFLOW_ENVIRONMENT", environment)
                .WithEnvironment("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://wf-prometheus:9090/api/v1/otlp/v1/metrics")
                .WithEnvironment("Engine__Enabled", engine ? "true" : "false")
                .WithEnvironment("Engine__ExecuteRuns", engine ? "true" : "false")
                .WithEnvironment("Engine__ApiEndpoint", "http://wf-api:8080")
                .WithEnvironment("Engine__PublicEndpoint", engine ? $"http://{name}:8081" : "http://localhost:8081")
                .WithEnvironment("Storage__Root", "/var/lib/wf/blobs")
                .WithEnvironment("Storage__UploadHmacKey", key)
                .WithEnvironment("Secrets__Provider", "Local")
                .WithEnvironment("Secrets__Root", "/var/lib/wf/secrets")
                .WithHttpEndpoint(targetPort: 8080, name: "api", env: "Api__Port")
                .WithHttpEndpoint(targetPort: 8081, name: "engine", env: "Engine__Port")
                .WithVolume("blobs", "/var/lib/wf/blobs")
                .WithVolume("control-secrets", "/var/lib/wf/secrets")
                .WaitForCompletion(migrations);
    }
}
