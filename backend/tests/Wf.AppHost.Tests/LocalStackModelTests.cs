using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
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
        Assert.Equal("dev", environment["WF_WORKFLOW_ENVIRONMENT"]);
        Assert.Equal("false", environment["Telemetry__Enabled"]);
        Assert.Equal(Path.Combine(root, "blobs"), environment["Storage__Root"]);
        Assert.Equal("Local", environment["Secrets__Provider"]);
        Assert.Equal(Path.Combine(root, "secrets"), environment["Secrets__Root"]);
        Assert.DoesNotContain("Lan__Enabled", environment.Keys);
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
        Assert.Equal("18.6-alpine", Assert.Single(postgres.Annotations.OfType<ContainerImageAnnotation>()).Tag);
        Assert.Equal("kardia-postgres-wf-test-volume", Assert.Single(postgres.Annotations.OfType<ContainerNameAnnotation>()).Name);
        var volume = Assert.Single(postgres.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(ContainerMountType.Volume, volume.Type);
        Assert.Equal("wf-test-volume", volume.Source);
        Assert.Equal("/var/lib/postgresql", volume.Target);
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
    public async Task Local_monitoring_has_loopback_bindings_persistent_data_and_the_configured_OTLP_receiver()
    {
        var builder = Builder(("Local:Monitoring", "true"), ("Local:PrometheusPort", "19090"),
            ("Local:GrafanaPort", "13000"), ("ConnectionStrings:wf", "Host=127.0.0.1;Database=workflow"));
        LocalStack.Configure(builder);
        await using var app = builder.Build();
        var prometheus = Assert.Single(builder.Resources.OfType<ContainerResource>(), resource => resource.Name == "wf-prometheus");
        var grafana = Assert.Single(builder.Resources.OfType<ContainerResource>(), resource => resource.Name == "wf-grafana");
        Assert.Equal("v3.15.0", Assert.Single(prometheus.Annotations.OfType<ContainerImageAnnotation>()).Tag);
        Assert.Equal("13.2.4", Assert.Single(grafana.Annotations.OfType<ContainerImageAnnotation>()).Tag);
        Assert.Equal("kardia-local-prometheus-19090", Assert.Single(prometheus.Annotations.OfType<ContainerNameAnnotation>()).Name);
        Assert.Equal("kardia-local-grafana-13000", Assert.Single(grafana.Annotations.OfType<ContainerNameAnnotation>()).Name);
        Assert.Contains(prometheus.Annotations.OfType<ContainerMountAnnotation>(), mount =>
            mount.Type == ContainerMountType.Volume && mount.Source == "wf-prometheus-data");
        Assert.Contains(grafana.Annotations.OfType<ContainerMountAnnotation>(), mount =>
            mount.Type == ContainerMountType.Volume && mount.Source == "wf-grafana-data");
        Assert.All(grafana.Annotations.OfType<ContainerMountAnnotation>().Where(mount => mount.Type == ContainerMountType.BindMount),
            mount => Assert.True(mount.IsReadOnly));
        foreach (var (resource, binding) in new[] { (prometheus, "127.0.0.1:19090:9090"), (grafana, "127.0.0.1:13000:3000") })
        {
            var arguments = new List<object>();
            foreach (var annotation in resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>())
                await annotation.Callback(new(arguments, TestContext.Current.CancellationToken));
            Assert.Equal(new object[] { "--publish", binding }, arguments);
        }
        var values = (await Configuration(Project(builder, "wf-host"))).EnvironmentVariables.ToDictionary();
        Assert.Equal("true", values["Telemetry__Enabled"]);
        Assert.Equal("http://127.0.0.1:19090/api/v1/otlp/v1/metrics", values["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
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
            Assert.Equal("wf-agent", environment["Agent__Labels__instance"]);
            Assert.Equal(Path.Combine(root, "secrets"), environment["Secrets__LocalDirectory"]);
            Assert.Equal(Path.Combine(root, "datasets"), environment["WF_DATASET_ROOT"]);
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
    [InlineData("Local:Agents", "0")]
    [InlineData("Local:Agents", "-1")]
    [InlineData("Local:Agents", "1.5")]
    [InlineData("Local:Agents", "")]
    [InlineData("Local:Agents", "2147483648")]
    [InlineData("Local:LinuxAgent", "not-a-boolean")]
    [InlineData("Local:Lan", "not-a-boolean")]
    [InlineData("Local:EnginePort", "0")]
    [InlineData("Local:EnginePort", "-1")]
    [InlineData("Local:ApiPort", "65536")]
    [InlineData("Local:UiPort", "65536")]
    [InlineData("Local:ApiPort", "not-a-port")]
    [InlineData("Local:EnginePort", "5000")]
    [InlineData("Local:UiPort", "5001")]
    [InlineData("Local:Monitoring", "not-a-boolean")]
    [InlineData("Local:PrometheusPort", "0")]
    [InlineData("Local:GrafanaPort", "65536")]
    [InlineData("Local:PrometheusPort", "5000")]
    [InlineData("Local:GrafanaPort", "9090")]
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

    [Fact]
    public void LocalStack_normalizes_short_flags_without_consuming_other_AppHost_options()
    {
        Assert.Equal(["--Local:Agents=3", "--Local:LinuxAgent=true", "--Local:ApiPort=5999"],
            AgentResources.NormalizeArguments(["--agents", "3", "--linux-agent", "--Local:ApiPort=5999"]));
        Assert.Equal(["--Local:Agents=2", "--Local:LinuxAgent=false"],
            AgentResources.NormalizeArguments(["--agents=2", "--linux-agent=false"]));
        Assert.Throws<ArgumentException>(() => AgentResources.NormalizeArguments(["--agents"]));
        Assert.Equal(["--Local:Lan=true", "--Local:Lan=false", "--Local:Agents=3"],
            AgentResources.NormalizeArguments(["--lan", "--lan=false", "--agents=3"]));
    }

    [Theory]
    [InlineData("Local:LanAddress", "127.0.0.1")]
    [InlineData("Local:LanAddress", "0.0.0.0")]
    [InlineData("Local:LanAddress", "224.1.2.3")]
    [InlineData("Local:LanAddress", "::1")]
    [InlineData("Local:LanAddress", "localhost")]
    [InlineData("Local:LanAddress", "10.1")]
    public void LocalStack_invalid_LAN_address_cannot_create_resources_or_identity(string key, string value)
    {
        var builder = Builder(("Local:Lan", "true"), (key, value));
        Assert.Throws<InvalidOperationException>(() => LocalStack.Configure(builder));
        Assert.Empty(builder.Resources);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task LocalStack_explicit_LAN_adds_only_certificate_configuration_without_exposing_HTTP_or_UI()
    {
        Directory.CreateDirectory(root);
        var certificate = Path.Combine(root, "certificate.pem");
        var key = Path.Combine(root, "private.pem");
        File.WriteAllText(certificate, "model fixture; real certificate validation is in LanListenerTests");
        File.WriteAllText(key, "model fixture");
        var address = LocalAddress();
        var builder = Builder(("Local:Lan", "true"), ("Local:LanAddress", address),
            ("Local:LanCertificate", certificate), ("Local:LanKey", key), ("ConnectionStrings:wf", "Host=127.0.0.1"));
        LocalStack.Configure(builder);
        await using var app = builder.Build();
        var host = Project(builder, "wf-host");
        var environment = (await Configuration(host)).EnvironmentVariables.ToDictionary();
        Assert.Equal("true", environment["Lan__Enabled"]);
        Assert.Equal(address, environment["Lan__Address"]);
        Assert.Equal("5001", environment["Lan__Port"]);
        Assert.Equal(certificate, environment["Lan__Certificate"]);
        Assert.Equal(key, environment["Lan__Key"]);
        Assert.Equal(["api", "engine"], host.Annotations.OfType<EndpointAnnotation>().Select(endpoint => endpoint.Name).Order());
        Assert.DoesNotContain("ASPNETCORE_URLS", environment.Keys);
        Assert.DoesNotContain("Lan__Enabled", (await Configuration(Project(builder, "wf-migrations")))
            .EnvironmentVariables.Select(variable => variable.Key));
        var ui = Assert.IsType<ExecutableResource>(Assert.Single(builder.Resources, resource => resource.Name == "wf-ui"));
        Assert.Contains((await Configuration(ui)).Arguments, argument => argument.Value == "127.0.0.1");
    }

    [Theory]
    [InlineData("Local:LanPort", "0")]
    [InlineData("Local:LanPort", "65536")]
    [InlineData("Local:LanPort", "not-a-port")]
    [InlineData("Local:LanCertificate", "")]
    [InlineData("Local:LanKey", "not-an-existing-key.pem")]
    public void LocalStack_invalid_LAN_port_or_certificate_pair_cannot_create_resources(string key, string value)
    {
        var builder = Builder(("Local:Lan", "true"), ("Local:LanAddress", LocalAddress()), (key, value));
        Assert.Throws<InvalidOperationException>(() => LocalStack.Configure(builder));
        Assert.Empty(builder.Resources);
        Assert.False(Directory.Exists(root));
    }

    private static string LocalAddress() =>
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address).First(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                !System.Net.IPAddress.IsLoopback(ip)).ToString();

    [Theory]
    [InlineData(5000)]
    [InlineData(5999)]
    public async Task LocalStack_three_native_and_one_Linux_agent_have_isolated_persistent_identities_labels_and_work_directories(int apiPort)
    {
        Dictionary<string, string>? identities = null;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var builder = Builder(("Local:Agents", "3"), ("Local:LinuxAgent", "true"), ("Local:ApiPort", apiPort.ToString()),
                ("ConnectionStrings:wf", "Host=127.0.0.1"));
            LocalStack.Configure(builder);
            await using var app = builder.Build();
            var resources = builder.Resources.Where(resource => resource is ProjectResource or ContainerResource &&
                resource.Name.StartsWith("wf-agent", StringComparison.Ordinal)).ToArray();
            Assert.Equal(4, resources.Length);
            Assert.Equal(3, resources.OfType<ProjectResource>().Count());
            var linux = Assert.Single(resources.OfType<ContainerResource>());
            var mounts = Assert.Single(linux.Annotations.OfType<ContainerMountAnnotation>());
            Assert.Equal(ContainerMountType.Volume, mounts.Type);
            Assert.Equal("/var/lib/wf-agent", mounts.Target);
            Assert.False(mounts.IsReadOnly);
            var current = new Dictionary<string, string>();
            var names = new HashSet<string>();
            var nativeWork = new HashSet<string>();
            foreach (var resource in resources)
            {
                var environment = (await Configuration(resource)).EnvironmentVariables.ToDictionary();
                Assert.Equal(resource.Name, environment["Agent__Labels__instance"]);
                Assert.True(names.Add(environment["Agent__Name"]));
                Assert.True(current.TryAdd(resource.Name, environment["Agent__Id"]));
                Assert.Equal("wf-host", Assert.Single(resource.Annotations.OfType<WaitAnnotation>()).Resource.Name);
                Assert.Contains("wf-host.bindings.engine", environment["Agent__Engines__0"]);
                if (resource == linux)
                {
                    Assert.Equal("container", environment["Agent__Labels__runtime"]);
                    Assert.Equal("true", environment["Agent__AllowInsecureTransport"]);
                    Assert.Equal("/var/lib/wf-agent/results", environment["Agent__ResultJournalDirectory"]);
                    Assert.Equal("/var/lib/wf-agent/secrets", environment["Secrets__LocalDirectory"]);
                    Assert.Equal("wf-local-agent-" + Guid.Parse(environment["Agent__Id"]).ToString("N"), mounts.Source);
                    Assert.Equal("kardia-local-agent-linux-" + apiPort,
                        Assert.Single(linux.Annotations.OfType<ContainerNameAnnotation>()).Name);
                }
                else
                {
                    Assert.Equal("native", environment["Agent__Labels__runtime"]);
                    Assert.True(nativeWork.Add(environment["Agent__ResultJournalDirectory"]));
                    Assert.Equal(Path.Combine(root, "secrets"), environment["Secrets__LocalDirectory"]);
                }
            }
            Assert.Equal(4, current.Values.Distinct().Count());
            if (identities is not null) Assert.Equal(identities.OrderBy(pair => pair.Key), current.OrderBy(pair => pair.Key));
            identities = current;
        }
    }

    [Fact]
    public void LocalStack_duplicate_identity_is_rejected_before_resources_are_added()
    {
        var id = Guid.NewGuid().ToString("D");
        foreach (var directory in new[] { "local", "local-2" })
        {
            var path = Path.Combine(root, "agents", directory);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "id"), id);
        }
        var builder = Builder(("Local:Agents", "2"));
        Assert.Throws<InvalidDataException>(() => LocalStack.Configure(builder));
        Assert.Empty(builder.Resources);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(17, false)]
    [InlineData(0, true)]
    public async Task Native_launcher_uses_checkout_relative_persistent_profile_and_forwards_failures(int exitCode, bool externalDatabase)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The native PowerShell launcher contract is checked on Windows.");
        Directory.CreateDirectory(root);
        var capture = Path.Combine(root, "arguments.json");
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = Path.GetTempPath(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", """
            function global:dotnet {
                [System.IO.File]::WriteAllText($env:WF_LAUNCH_CAPTURE, (ConvertTo-Json -InputObject @($args) -Compress))
                $global:LASTEXITCODE = [int]$env:WF_LAUNCH_EXIT
            }
            function global:bun {}
            function global:docker {}
            & $env:WF_LAUNCH_SCRIPT
            exit $LASTEXITCODE
            """ })
            start.ArgumentList.Add(argument);
        start.Environment.Remove("ConnectionStrings__wf");
        start.Environment.Remove("ConnectionStrings:wf");
        if (externalDatabase) start.Environment["ConnectionStrings__wf"] = "Host=must-not-connect.invalid";
        start.Environment["WF_LAUNCH_SCRIPT"] = Path.Combine(Repository(), "start-local.ps1");
        start.Environment["WF_LAUNCH_CAPTURE"] = capture;
        start.Environment["WF_LAUNCH_EXIT"] = exitCode.ToString();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var diagnostic = await stderr;
            Assert.Equal(externalDatabase ? 1 : exitCode, process.ExitCode);
            Assert.Empty(await stdout);
            if (externalDatabase)
            {
                Assert.Contains("Clear ConnectionStrings__wf", diagnostic);
                Assert.False(File.Exists(capture));
            }
            else
            {
                Assert.Empty(diagnostic);
                var actual = Assert.IsType<string[]>(JsonSerializer.Deserialize<string[]>(
                    await File.ReadAllTextAsync(capture, deadline.Token)));
                Assert.Equal([
                    "run", "--project", Path.Combine(Repository(), "backend", "src", "Wf.AppHost", "Wf.AppHost.csproj"),
                    "--", "--Local:DataRoot=" + Path.Combine(Repository(), ".wf", "native-main"),
                    "--Local:PostgresVolume=wf-native-main-pg18", "--Local:ApiPort=5000",
                    "--Local:EnginePort=5001", "--Local:UiPort=5173", "--Local:Monitoring=false"
                ], actual);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
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
