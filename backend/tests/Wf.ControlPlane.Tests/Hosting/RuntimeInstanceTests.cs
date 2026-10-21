using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Tests.Hosting;

public sealed class RuntimeInstanceTests
{
    [Fact]
    public void Identity_is_per_process_and_downward_API_metadata_is_preserved()
    {
        var configuration = Configuration(new()
        {
            ["Runtime:Name"] = "kardia-controller-0",
            ["Runtime:HostName"] = "container-host",
            ["NODE_NAME"] = "kardia-lab-1",
            ["POD_NAME"] = "kardia-controller-0",
            ["POD_NAMESPACE"] = "kardia"
        });
        var first = RuntimeInstance.Read(configuration, true);
        var second = RuntimeInstance.Read(configuration, true);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(Guid.TryParseExact(first.Id, "N", out _));
        Assert.Equal("kardia-controller-0", first.Name);
        Assert.Equal("container-host", first.HostName);
        Assert.Equal("kardia-lab-1", first.NodeName);
        Assert.Equal("kardia-controller-0", first.PodName);
        Assert.Equal("kardia", first.Namespace);
        Assert.True(first.EngineEnabled);
        Assert.NotEmpty(first.Version);
    }

    [Theory]
    [InlineData(true, "controller")]
    [InlineData(false, "api")]
    public void Native_defaults_distinguish_our_roles_without_inventing_a_pod(bool engine, string role)
    {
        var instance = RuntimeInstance.Read(Configuration(new() { ["Runtime:HostName"] = "native-host" }), engine);
        Assert.Equal("kardia-" + role + "-native-host", instance.Name);
        Assert.Equal("native-host", instance.NodeName);
        Assert.Null(instance.PodName);
        Assert.Null(instance.Namespace);
    }

    [Theory]
    [InlineData("Runtime:Name", "")]
    [InlineData("Runtime:HostName", " host")]
    [InlineData("Runtime:NodeName", "node\nother")]
    [InlineData("Runtime:PodName", " ")]
    [InlineData("Runtime:Namespace", "kardia ")]
    public void Invalid_labels_fail_before_startup(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => RuntimeInstance.Read(Configuration(new() { [key] = value }), false));

    [Fact]
    public void Oversized_labels_fail_instead_of_silently_truncating() =>
        Assert.Throws<InvalidOperationException>(() => RuntimeInstance.Read(
            Configuration(new() { ["Runtime:Name"] = new string('a', 201) }), false));

    [Fact]
    public async Task Only_actual_API_and_live_requests_increment_the_instance_counter_including_errors()
    {
        var instance = RuntimeInstance.Read(Configuration(new()), false);
        var activity = new RuntimeActivity();
        await using var services = new ServiceCollection().AddSingleton(instance).AddSingleton(activity).BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseRuntimeActivity();
        builder.Run(context => context.Request.Path == "/api/v1/failure"
            ? throw new IOException("test failure") : Task.CompletedTask);
        var pipeline = builder.Build();
        foreach (var path in new[] { "/healthz", "/healthz/ready", "/other", "/api/v10/not-an-api", "/hubs/live/v1-evil" })
        {
            var context = Context(path, services);
            await pipeline(context);
            Assert.False(context.Response.Headers.ContainsKey("X-Kardia-Instance"));
        }
        Assert.Equal(0, activity.HttpRequests);
        var api = Context("/api/v1/admin/system", services);
        await pipeline(api);
        Assert.Equal(instance.Id, api.Response.Headers["X-Kardia-Instance"]);
        await pipeline(Context("/hubs/live/v1/negotiate", services));
        await Assert.ThrowsAsync<IOException>(() => pipeline(Context("/api/v1/failure", services)));
        Assert.Equal(3, activity.HttpRequests);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => pipeline(Context("/api/v1/runs", services))));
        Assert.Equal(103, activity.HttpRequests);
    }

    private static DefaultHttpContext Context(string path, IServiceProvider services) =>
        new() { RequestServices = services, Request = { Path = path } };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
