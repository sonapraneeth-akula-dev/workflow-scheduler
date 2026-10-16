using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Wf.AppHost.Tests;

public sealed class LocalEdgeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "LocalEdge")]
    public async Task Both_existing_edge_environments_use_trusted_HTTP2_and_the_native_Windows_agent_joins_PPE()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_EDGE_SMOKE") == "true",
            "Set WF_EDGE_SMOKE=true and WF_EDGE_PLATFORM_ROOT after starting both stacks and joining the native PPE validation agent.");
        var platform = Environment.GetEnvironmentVariable("WF_EDGE_PLATFORM_ROOT") ??
            throw new InvalidOperationException("WF_EDGE_PLATFORM_ROOT must identify the existing GrihaSetu platform checkout.");
        var source = Path.Combine(LocalStackModelTests.Repository(), "infra", "local", "edge", "kardia.caddy");
        var deployed = Path.Combine(platform, "edge", "services", "kardia.caddy");
        var token = TestContext.Current.CancellationToken;
        Assert.Equal((await File.ReadAllTextAsync(source, token)).ReplaceLineEndings("\n"),
            (await File.ReadAllTextAsync(deployed, token)).ReplaceLineEndings("\n"));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        foreach (var environment in new[] { "dev", "ppe" })
        {
            var origin = $"https://{environment}.kardia.sonapraneeth.in";
            using var health = await client.GetAsync(origin + "/healthz", token);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpVersion.Version20, health.Version);
            using var page = await client.GetAsync(origin, token);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("<div id=\"root\">", await page.Content.ReadAsStringAsync(token));
            var inventory = await client.GetFromJsonAsync<JsonElement>(origin + "/api/v1/machines?status=Healthy", token);
            var machines = inventory.GetProperty("items").EnumerateArray().ToArray();
            Assert.Contains(machines, machine => machine.GetProperty("os").GetString() == "linux" &&
                machine.GetProperty("labels").GetProperty("environment").GetString() == environment);
            if (environment == "ppe")
            {
                var native = Assert.Single(machines, machine => machine.GetProperty("os").GetString() == "windows" &&
                    machine.GetProperty("pool").GetString() == "edge-validation" &&
                    machine.GetProperty("labels").TryGetProperty("transport", out var transport) &&
                    transport.GetString() == "edge-tls");
                output.WriteLine("Actual native Windows TLS/gRPC registration: " + native.GetProperty("id").GetGuid());
            }
            output.WriteLine($"{environment}: default trusted TLS HTTP/2 health=200, SPA=200, real agent inventory=200.");
        }
    }
}
