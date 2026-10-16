using System.Runtime.CompilerServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.AppHost;

namespace Wf.AppHost.Tests;

public sealed class LocalStackModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wf-apphost-model-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalStack_external_database_has_no_container_maps_connection_and_orders_startup()
    {
        var builder = Builder(("ConnectionStrings:wf", "Host=127.0.0.1;Database=workflow;Username=developer"));
        LocalStack.Configure(builder);
        await using var app = builder.Build();
        Assert.Empty(builder.Resources.OfType<ContainerResource>());
        var host = Project(builder, "wf-host");
        var configuration = await Configuration(host);
        var environment = configuration.EnvironmentVariables.ToDictionary();
        Assert.Equal("{wf.connectionString}", environment["ConnectionStrings__Workflow"]);
        Assert.Equal("Development", environment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Development", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal(Path.Combine(root, "blobs"), environment["Storage__Root"]);
        Assert.Equal("Local", environment["Secrets__Provider"]);
        Assert.Equal(Path.Combine(root, "secrets"), environment["Secrets__Root"]);
        Assert.Contains("storage-key", environment["Storage__UploadHmacKey"]);
        Assert.Equal(["--dev"], configuration.Arguments.Select(argument => argument.Value));
        var migrations = Project(builder, "wf-migrations");
        Assert.Equal(["--migrate"], (await Configuration(migrations)).Arguments.Select(argument => argument.Value));
        var migrationWait = Assert.Single(host.Annotations.OfType<WaitAnnotation>());
        Assert.Same(migrations, migrationWait.Resource);
        Assert.Equal(WaitType.WaitForCompletion, migrationWait.WaitType);
        Assert.Equal(0, migrationWait.ExitCode);
        Assert.Empty(migrations.Annotations.OfType<WaitAnnotation>());
        Assert.Equal(["api", "engine"], host.Annotations.OfType<EndpointAnnotation>().Select(endpoint => endpoint.Name).Order());
        Assert.All(host.Annotations.OfType<EndpointAnnotation>(), endpoint => Assert.False(endpoint.IsProxied));
        Assert.Equal(5000, Assert.Single(host.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "api").Port);
        Assert.Equal(5001, Assert.Single(host.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "engine").Port);
    }

    [Fact]
    public async Task LocalStack_default_postgres_has_persistent_volume_and_migrations_wait_for_database()
    {
        var builder = Builder(("Local:PostgresVolume", "wf-test-volume"));
        LocalStack.Configure(builder);
        await using var app = builder.Build();
        var postgres = Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        Assert.Equal("17-alpine", Assert.Single(postgres.Annotations.OfType<ContainerImageAnnotation>()).Tag);
        var volume = Assert.Single(postgres.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(ContainerMountType.Volume, volume.Type);
        Assert.Equal("wf-test-volume", volume.Source);
        Assert.False(volume.IsReadOnly);
        var wait = Assert.Single(Project(builder, "wf-migrations").Annotations.OfType<WaitAnnotation>());
        Assert.Same(postgres, wait.Resource);
        Assert.Equal(WaitType.WaitUntilHealthy, wait.WaitType);
        Assert.Contains(builder.Resources, resource => resource.Name == "wf" && resource is PostgresDatabaseResource);
        var key = Assert.Single(builder.Resources.OfType<ParameterResource>(), resource => resource.Name == "storage-key");
        Assert.True(key.Secret);
        Assert.NotNull(key.Default);
        Assert.Equal("UserSecretsParameterDefault", key.Default.GetType().Name);
    }

    [Fact]
    public async Task LocalStack_native_agent_identity_and_all_disk_paths_survive_recreating_apphost()
    {
        string? identity = null;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var builder = Builder(("ConnectionStrings:wf", "Host=127.0.0.1;Database=workflow"));
            LocalStack.Configure(builder);
            await using var app = builder.Build();
            var agent = Project(builder, "wf-agent");
            var environment = (await Configuration(agent)).EnvironmentVariables.ToDictionary();
            var current = environment["Agent__Id"];
            Assert.True(Guid.TryParseExact(current, "D", out var parsed));
            Assert.NotEqual(Guid.Empty, parsed);
            Assert.Equal(identity ?? current, current);
            identity = current;
            Assert.Equal(current, File.ReadAllText(Path.Combine(root, "agents", "local", "id")));
            Assert.Equal("default", environment["Agent__Pool"]);
            Assert.Equal("local", environment["Agent__Labels__environment"]);
            Assert.Contains("wf-host.bindings.engine", environment["Agent__Engines__0"]);
            foreach (var (key, leaf) in new[] { ("ResultJournalDirectory", "results"), ("LogSpoolDirectory", "logs"), ("PackageCache", "packages") })
                Assert.Equal(Path.Combine(root, "agents", "local", leaf), environment["Agent__" + key]);
            Assert.Equal("wf-host", Assert.Single(agent.Annotations.OfType<WaitAnnotation>()).Resource.Name);
        }
    }

    [Fact]
    public async Task LocalStack_ui_uses_existing_bun_without_install_loopback_proxy_and_login_free_development()
    {
        var builder = Builder(("Local:UiPort", "5199"), ("ConnectionStrings:wf", "Host=127.0.0.1"));
        LocalStack.Configure(builder);
        await using var app = builder.Build();
        var ui = Assert.IsType<ExecutableResource>(Assert.Single(builder.Resources, resource => resource.Name == "wf-ui"));
        Assert.Equal("wf-ui", ui.Name);
        Assert.Equal("bun", ui.Command);
        Assert.Equal(Path.Combine(Repository(), "frontend"), ui.WorkingDirectory);
        var configuration = await Configuration(ui);
        var arguments = configuration.Arguments.Select(argument => argument.Value);
        Assert.Equal(["run", "dev", "--host", "127.0.0.1", "--port", "{wf-ui.bindings.http.targetPort}", "--strictPort"], arguments);
        var environment = configuration.EnvironmentVariables.ToDictionary();
        Assert.Equal("development", environment["VITE_AUTH_MODE"]);
        Assert.Contains("wf-host.bindings.api", environment["KARDIA_API_URL"]);
        Assert.Equal(5199, Assert.Single(ui.Annotations.OfType<EndpointAnnotation>()).Port);
        Assert.Equal("wf-host", Assert.Single(ui.Annotations.OfType<WaitAnnotation>()).Resource.Name);
    }

    [Theory]
    [InlineData("Local:EnginePort", "0")]
    [InlineData("Local:EnginePort", "-1")]
    [InlineData("Local:ApiPort", "65536")]
    [InlineData("Local:UiPort", "65536")]
    [InlineData("Local:ApiPort", "not-a-port")]
    [InlineData("Local:EnginePort", "5000")]
    [InlineData("Local:UiPort", "5001")]
    [InlineData("ConnectionStrings:wf", " ")]
    [InlineData("Local:DataRoot", " ")]
    public void LocalStack_invalid_configuration_fails_before_creating_processes_or_identity(string key, string value)
    {
        var builder = Builder((key, value));
        Assert.Throws<InvalidOperationException>(() => LocalStack.Configure(builder));
        Assert.Empty(builder.Resources);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void LocalStack_corrupt_identity_is_not_silently_replaced(string value)
    {
        var directory = Path.Combine(root, "agents", "local");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "id");
        File.WriteAllText(file, value);
        var builder = Builder();
        Assert.Throws<InvalidDataException>(() => LocalStack.Configure(builder));
        Assert.Equal(value, File.ReadAllText(file));
        Assert.Empty(builder.Resources);
    }

    private IDistributedApplicationBuilder Builder(params (string Key, string Value)[] settings)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(LocalStack).Assembly.GetName().Name,
            ProjectDirectory = Path.Combine(Repository(), "backend", "src", "Wf.AppHost"),
            DisableDashboard = true
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Local:DataRoot"] = root });
        foreach (var (key, value) in settings) builder.Configuration[key] = value;
        return builder;
    }

    private static ProjectResource Project(IDistributedApplicationBuilder builder, string name) =>
        Assert.IsType<ProjectResource>(Assert.Single(builder.Resources, resource => resource.Name == name));

    private static async Task<IExecutionConfigurationResult> Configuration(IResource resource)
    {
        var result = await ExecutionConfigurationBuilder.Create(resource).WithArgumentsConfig().WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(
                DistributedApplicationOperation.Publish)), NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Null(result.Exception);
        return result;
    }

    internal static string Repository([CallerFilePath] string file = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "Wf.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("AppHost tests require the repository source tree.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
