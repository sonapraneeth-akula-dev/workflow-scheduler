using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Wf.AppHost;

public static class LocalStack
{
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.ExecutionContext.IsRunMode)
            throw new InvalidOperationException("This AppHost currently supports local development only; deployment profiles are separate tasks.");
        var enginePort = Port("Engine", 5001);
        var apiPort = Port("Api", 5000);
        var uiPort = Port("Ui", 5173);
        if (new[] { enginePort, apiPort, uiPort }.Distinct().Count() != 3)
            throw new InvalidOperationException("Local engine, API and UI ports must differ.");
        var external = builder.Configuration.GetConnectionString("wf");
        if (external is not null && string.IsNullOrWhiteSpace(external))
            throw new InvalidOperationException("ConnectionStrings:wf must be a non-empty PostgreSQL connection string.");
        var configuredRoot = builder.Configuration["Local:DataRoot"];
        if (configuredRoot is not null && string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("Local:DataRoot cannot be empty.");
        var root = Path.GetFullPath(configuredRoot ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wf"));
        var lan = LanResources.Read(builder.Configuration, root);
        var agents = AgentResources.ReadIdentities(builder.Configuration, root);

        IResourceBuilder<IResourceWithConnectionString> database;
        IResourceBuilder<PostgresServerResource>? postgres = null;
        if (external is null)
        {
            postgres = builder.AddPostgres("postgres").WithImageTag("17-alpine")
                .WithDataVolume(builder.Configuration["Local:PostgresVolume"] ?? "wf-postgres-data");
            database = postgres.AddDatabase("wf", "workflow");
        }
        else database = builder.AddConnectionString("wf");
        var key = builder.AddParameter("storage-key",
            new GenerateParameterDefault { MinLength = 64, Special = false }, secret: true, persist: true);
        var migrations = builder.AddProject<Projects.Wf_ControlPlane>("wf-migrations", launchProfileName: null)
            .WithReference(database, connectionName: "Workflow")
            .WithArgs("--migrate");
        if (postgres is not null) migrations.WaitFor(postgres);
        var host = builder.AddProject<Projects.Wf_ControlPlane>("wf-host", launchProfileName: null)
            .WithReference(database, connectionName: "Workflow")
            .WithArgs("--dev")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("Engine__ExecuteRuns", "true")
            .WithEnvironment("Storage__Root", Path.Combine(root, "blobs"))
            .WithEnvironment("Storage__UploadHmacKey", key)
            .WithEnvironment("Secrets__Provider", "Local")
            .WithEnvironment("Secrets__Root", Path.Combine(root, "secrets"))
            .WithHttpEndpoint(port: apiPort, targetPort: apiPort, name: "api", env: "Api__Port", isProxied: false)
            .WithHttpEndpoint(port: enginePort, targetPort: enginePort, name: "engine", env: "Engine__Port", isProxied: false)
            .WithHttpHealthCheck("/healthz/leader", endpointName: "api")
            .WaitForCompletion(migrations);
        if (lan is not null)
            host.WithEnvironment("Lan__Enabled", "true")
                .WithEnvironment("Lan__Address", lan.Address)
                .WithEnvironment("Lan__Port", lan.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithEnvironment("Lan__Certificate", lan.Certificate)
                .WithEnvironment("Lan__Key", lan.Key);
        AgentResources.Add(builder, host, root, agents);
        var frontend = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "frontend"));
        var ui = builder.AddExecutable("wf-ui", "bun", frontend)
            .WithHttpEndpoint(port: uiPort, targetPort: uiPort, isProxied: false)
            .WithHttpHealthCheck("/")
            .WithEnvironment("KARDIA_API_URL", host.GetEndpoint("api"))
            .WithEnvironment("VITE_AUTH_MODE", "development")
            .WaitFor(host);
        ui.WithArgs("run", "dev", "--host", "127.0.0.1", "--port",
            ui.GetEndpoint("http").Property(EndpointProperty.TargetPort), "--strictPort");

        int Port(string name, int fallback)
        {
            var port = builder.Configuration.GetValue($"Local:{name}Port", fallback);
            return port is >= 1 and <= 65535 ? port :
                throw new InvalidOperationException($"Local:{name}Port must be between 1 and 65535.");
        }
    }
}
