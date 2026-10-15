using Microsoft.Extensions.Configuration;

namespace Wf.Agent.Tests;

public sealed class AgentOptionsTests
{
    [Theory]
    [InlineData("file:///engine")]
    [InlineData("http://engine:5000")]
    [InlineData("https://user:password@engine")]
    [InlineData("https://engine/path")]
    [InlineData("https://engine?token=secret")]
    [InlineData("https://engine#fragment")]
    public void Invalid_or_unsafe_endpoints_are_rejected(string endpoint) =>
        Assert.Throws<ArgumentException>(() => Options([new Uri(endpoint)]));

    [Fact]
    public void Empty_endpoints_and_capacity_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Options([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentOptions([new Uri("https://engine")], "id", "name", "pool", 0));
        Assert.Throws<ArgumentException>(() => new AgentOptions([new Uri("https://engine")], "", "name", "pool", 1));
    }

    [Fact]
    public void Configuration_supports_endpoint_lists_identity_capacity_and_labels()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Engines:0"] = "https://engine-1", ["Agent:Engines:1"] = "https://engine-2",
            ["Agent:Id"] = "test-worker", ["Agent:Name"] = "Test", ["Agent:Pool"] = "ci",
            ["Agent:MaxTasks"] = "2", ["Agent:Labels:dotnet"] = "10"
        }).Build();
        var options = AgentOptions.FromConfiguration(configuration);
        Assert.Equal(2, options.Engines.Count);
        Assert.Equal("test-worker", options.Id);
        Assert.Equal("Test", options.Name);
        Assert.Equal("ci", options.Pool);
        Assert.Equal(2U, options.MaxTasks);
        Assert.Equal("10", options.Labels["dotnet"]);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HeartbeatInterval);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Invalid_capacity_is_not_silently_defaulted(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Engines:0"] = "http://localhost:5000", ["Agent:MaxTasks"] = value
        }).Build();
        Assert.ThrowsAny<ArgumentException>(() => AgentOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void Inputs_are_copied_and_insecure_networks_need_explicit_opt_in()
    {
        var labels = new Dictionary<string, string> { ["os"] = "test" };
        var engines = new[] { new Uri("http://engine") };
        var options = new AgentOptions(engines, "id", "name", "pool", 1, labels, allowInsecureTransport: true);
        engines[0] = new Uri("https://other");
        labels["os"] = "changed";
        Assert.Equal("http://engine/", options.Engines.Single().AbsoluteUri);
        Assert.Equal("test", options.Labels["os"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Heartbeat_must_fit_health_window(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentOptions([new Uri("https://engine")],
            "id", "name", "pool", 1, heartbeatInterval: TimeSpan.FromSeconds(seconds)));

    private static AgentOptions Options(Uri[] endpoints) => new(endpoints, "id", "name", "pool", 1);
}
