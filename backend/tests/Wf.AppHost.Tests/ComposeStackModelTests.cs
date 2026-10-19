using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.AppHost;

namespace Wf.AppHost.Tests;

public sealed class ComposeStackModelTests
{
    [Theory]
    [InlineData("dev")]
    [InlineData("ppe")]
    public async Task Publisher_models_API_two_engines_stable_Linux_agent_and_migration_gates(string environment)
    {
        var builder = Builder(environment);
        Assert.True(builder.ExecutionContext.IsPublishMode);
        LocalStack.Configure(builder);
        await using var app = builder.Build();
        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        Assert.Empty(builder.Resources.OfType<ExecutableResource>());
        var hosts = builder.Resources.OfType<ContainerResource>()
            .Where(resource => resource.Name is "wf-api" or "wf-engine-a" or "wf-engine-b").ToArray();
        Assert.Equal(3, hosts.Length);
        foreach (var host in hosts)
        {
            var values = (await Configuration(host)).EnvironmentVariables.ToDictionary();
            Assert.Equal(host.Name == "wf-api" ? "false" : "true", values["Engine__Enabled"]);
            Assert.Equal(host.Name == "wf-api" ? "false" : "true", values["Engine__ExecuteRuns"]);
            Assert.Equal("Development", values["DOTNET_ENVIRONMENT"]);
            Assert.Equal("Development", values["Authentication__Mode"]);
            Assert.Equal("true", values["LocalCompose__Enabled"]);
            Assert.Equal(environment, values["LocalCompose__Environment"]);
            Assert.Equal(environment, values["WF_WORKFLOW_ENVIRONMENT"]);
            Assert.Equal("http://wf-prometheus:9090/api/v1/otlp/v1/metrics", values["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
            Assert.Equal("http://wf-api:8080", values["Engine__ApiEndpoint"]);
            Assert.Equal(host.Name == "wf-api" ? "http://localhost:8081" : $"http://{host.Name}:8081",
                values["Engine__PublicEndpoint"]);
            Assert.Contains("storage-key", values["Storage__UploadHmacKey"]);
            var wait = Assert.Single(host.Annotations.OfType<WaitAnnotation>());
            Assert.Equal("wf-migrations", wait.Resource.Name);
            Assert.Equal(WaitType.WaitForCompletion, wait.WaitType);
            Assert.Equal(0, wait.ExitCode);
            Assert.All(host.Annotations.OfType<ContainerMountAnnotation>(), mount =>
                Assert.Equal(ContainerMountType.Volume, mount.Type));
        }
        var agent = Assert.Single(builder.Resources.OfType<ContainerResource>(),
            resource => resource.Name == "wf-agent-linux");
        var agentValues = (await Configuration(agent)).EnvironmentVariables.ToDictionary();
        Assert.Contains("agent-id", agentValues["Agent__Id"]);
        Assert.Equal("true", agentValues["Agent__AllowInsecureTransport"]);
        Assert.Equal("http://wf-api:8080", agentValues["Agent__InsecurePackageOrigin"]);
        Assert.Contains("wf-gateway", agentValues["Agent__Engines__0"]);
        Assert.False(agentValues.ContainsKey("Agent__Engines__1"));
        var gateway = Assert.Single(builder.Resources.OfType<ContainerResource>(),
            resource => resource.Name == "wf-gateway");
        Assert.Equal(8080, Assert.Single(gateway.Annotations.OfType<EndpointAnnotation>()).TargetPort);
        Assert.Equal(new[] { "wf-api", "wf-engine-a", "wf-engine-b" },
            gateway.Annotations.OfType<WaitAnnotation>().Select(wait => wait.Resource.Name).Order().ToArray());
        Assert.Equal("wf-gateway", Assert.Single(agent.Annotations.OfType<WaitAnnotation>()).Resource.Name);
        Assert.Equal(environment, agentValues["Agent__Labels__environment"]);
        Assert.Equal("/var/lib/wf-agent/results", agentValues["Agent__ResultJournalDirectory"]);
        var postgres = Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        Assert.Equal("18.6-alpine", Assert.Single(postgres.Annotations.OfType<ContainerImageAnnotation>()).Tag);
        Assert.Equal("postgres-data", Assert.Single(postgres.Annotations.OfType<ContainerMountAnnotation>()).Source);
        Assert.Equal("/var/lib/postgresql", Assert.Single(postgres.Annotations.OfType<ContainerMountAnnotation>()).Target);
        Assert.All(builder.Resources.OfType<ParameterResource>().Where(parameter => parameter.Name != "agent-id"),
            parameter => Assert.True(parameter.Secret));
    }

    [Theory]
    [InlineData("production", null, null)]
    [InlineData("PPE", null, null)]
    [InlineData("ppe", "true", null)]
    [InlineData("ppe", null, "Host=localhost;Database=existing")]
    public void Invalid_environment_or_native_overrides_cannot_add_any_resources(string name, string? lan, string? database)
    {
        var builder = Builder(name);
        builder.Configuration["Local:Lan"] = lan;
        builder.Configuration["ConnectionStrings:wf"] = database;
        Assert.Throws<InvalidOperationException>(() => LocalStack.Configure(builder));
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void Run_mode_cannot_accidentally_enable_the_container_publish_profile()
    {
        var builder = Builder("ppe", publish: false);
        Assert.Throws<InvalidOperationException>(() => LocalStack.Configure(builder));
        Assert.Empty(builder.Resources);
    }

    private static IDistributedApplicationBuilder Builder(string environment, bool publish = true)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(LocalStack).Assembly.GetName().Name,
            ProjectDirectory = Path.Combine(LocalStackModelTests.Repository(), "backend", "src", "Wf.AppHost"),
            DisableDashboard = true,
            Args = publish ? ["--operation", "publish"] : []
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Local:Compose"] = "true", ["Local:ComposeEnvironment"] = environment
        });
        return builder;
    }

    private static async Task<IExecutionConfigurationResult> Configuration(IResource resource)
    {
        var result = await ExecutionConfigurationBuilder.Create(resource).WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(
                DistributedApplicationOperation.Publish)), NullLogger.Instance,
                TestContext.Current.CancellationToken);
        Assert.Null(result.Exception);
        return result;
    }
}
