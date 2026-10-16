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
        var agentRoot = Path.Combine(root, "agents", "local");
        Directory.CreateDirectory(agentRoot);
        var identityFile = Path.Combine(agentRoot, "id");
        if (!File.Exists(identityFile)) File.WriteAllText(identityFile, Guid.NewGuid().ToString("D"));
        if (!Guid.TryParseExact(File.ReadAllText(identityFile), "D", out var identity) || identity == Guid.Empty)
            throw new InvalidDataException("The local agent identity file must contain a non-empty canonical UUID.");

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
            .WithEnvironment("Storage__Root", Path.Combine(root, "blobs"))
            .WithEnvironment("Storage__UploadHmacKey", key)
            .WithEnvironment("Secrets__Provider", "Local")
            .WithEnvironment("Secrets__Root", Path.Combine(root, "secrets"))
            .WithHttpEndpoint(port: apiPort, targetPort: apiPort, name: "api", env: "Api__Port", isProxied: false)
            .WithHttpEndpoint(port: enginePort, targetPort: enginePort, name: "engine", env: "Engine__Port", isProxied: false)
            .WithHttpHealthCheck("/healthz/leader", endpointName: "api")
            .WaitForCompletion(migrations);
        builder.AddProject<Projects.Wf_Agent>("wf-agent", launchProfileName: null)
            .WithEnvironment("Agent__Id", identity.ToString("D"))
            .WithEnvironment("Agent__Name", "local-" + identity.ToString("N"))
            .WithEnvironment("Agent__Pool", "default")
            .WithEnvironment("Agent__Labels__environment", "local")
            .WithEnvironment("Agent__Engines__0", host.GetEndpoint("engine"))
            .WithEnvironment("Agent__ResultJournalDirectory", Path.Combine(agentRoot, "results"))
            .WithEnvironment("Agent__LogSpoolDirectory", Path.Combine(agentRoot, "logs"))
            .WithEnvironment("Agent__PackageCache", Path.Combine(agentRoot, "packages"))
            .WithEnvironment("Secrets__Provider", "Local")
            .WithEnvironment("Secrets__Root", Path.Combine(root, "secrets"))
            .WaitFor(host);
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
