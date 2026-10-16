using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wf.Integration.Tests;

public sealed class LocalComposeLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "ComposeLifecycle")]
    public async Task Public_CLI_environments_coexist_and_scoped_down_preserves_database_blobs_identity_and_keys()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_COMPOSE_LIFECYCLE") == "true",
            "Set WF_COMPOSE_LIFECYCLE=true with Aspire 13.6, Docker and the Debug CLI built. Both kardia projects must be unused.");
        var repository = Repository();
        var cli = Path.Combine(repository, "backend", "src", "Wf.Cli", "bin", "Debug", "net10.0", "wf.dll");
        Assert.True(File.Exists(cli), "Build backend\\src\\Wf.Cli in normal Debug configuration first.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var token = deadline.Token;
        foreach (var environment in Environments)
        {
            Assert.Empty(await Resources("ps", environment, token));
            Assert.Empty(await Resources("volume", environment, token));
        }
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        var started = new HashSet<string>();
        try
        {
            started.Add("ppe");
            await Local("up", null, token);
            var ppe = await HealthyMachine("ppe", token);
            var ppeState = StateHash("ppe");
            var session = await Sql("ppe", "SELECT session_id FROM agents", token);
            var marker = "compose_lifecycle_" + Guid.NewGuid().ToString("N");
            await Sql("ppe", $"CREATE TABLE {marker}(value integer); INSERT INTO {marker} VALUES(42)", token);
            await Command("docker", ["exec", Container("ppe", "wf-api"), "sh", "-c",
                $"printf persisted > /var/lib/wf/blobs/{marker}"], token);
            await Command("docker", ["exec", Container("ppe", "wf-agent-linux"), "sh", "-c",
                $"printf persisted > /var/lib/wf-agent/{marker}"], token);

            started.Add("dev");
            await Local("up", "dev", token);
            var dev = await HealthyMachine("dev", token);
            Assert.NotEqual(ppe, dev);
            var devState = StateHash("dev");
            foreach (var environment in Environments)
            {
                var bindings = new Dictionary<string, int>
                {
                    ["wf-api"] = 5000, ["wf-engine-a"] = 5001,
                    ["wf-engine-b"] = 5002, ["compose-dashboard"] = 18888
                };
                foreach (var (service, port) in bindings)
                {
                    var json = await Command("docker", ["inspect", "--format",
                        "{{json .NetworkSettings.Ports}}", Container(environment, service)], token);
                    using var ports = JsonDocument.Parse(json);
                    var published = ports.RootElement.EnumerateObject()
                        .Where(property => property.Value.ValueKind == JsonValueKind.Array)
                        .SelectMany(property => property.Value.EnumerateArray()).ToArray();
                    var binding = Assert.Single(published);
                    Assert.Equal(Address(environment), binding.GetProperty("HostIp").GetString());
                    Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        binding.GetProperty("HostPort").GetString());
                }
                using var dashboard = await client.GetAsync($"http://{Address(environment)}:18888/", token);
                Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
                using var apiLeader = await client.GetAsync($"http://{Address(environment)}:5000/healthz/leader", token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, apiLeader.StatusCode);
                var hint = await Sql(environment, "SELECT leader_hint FROM leases", token);
                Assert.Contains(hint, new[] { "http://wf-engine-a:8081/", "http://wf-engine-b:8081/" });
                var leaderCodes = new List<string>();
                foreach (var engine in new[] { "wf-engine-a", "wf-engine-b" })
                    leaderCodes.Add(await Command("docker", ["exec", Container(environment, engine),
                        "curl", "--silent", "--output", "/dev/null", "--write-out", "%{http_code}",
                        "http://127.0.0.1:8080/healthz/leader"], token));
                Assert.Equal(new[] { "200", "503" }, leaderCodes.Order().ToArray());
            }
            var projects = await Command("docker", ["compose", "ls", "--format", "json"], token);
            using (var document = JsonDocument.Parse(projects))
                foreach (var environment in Environments)
                    Assert.Contains(document.RootElement.EnumerateArray(),
                        project => project.GetProperty("Name").GetString() == "kardia-" + environment);

            await Local("down", null, token);
            Assert.Empty(await Resources("ps", "ppe", token));
            Assert.Equal(4, (await Resources("volume", "ppe", token)).Length);
            Assert.Equal(dev, await HealthyMachine("dev", token));
            await Local("up", "ppe", token);
            Assert.Equal(ppe, await HealthyMachine("ppe", token));
            Assert.NotEqual(session, await Sql("ppe", "SELECT session_id FROM agents", token));
            Assert.Equal("42", await Sql("ppe", $"SELECT value FROM {marker}", token));
            Assert.Equal("persisted", await Command("docker",
                ["exec", Container("ppe", "wf-api"), "cat", "/var/lib/wf/blobs/" + marker], token));
            Assert.Equal("persisted", await Command("docker",
                ["exec", Container("ppe", "wf-agent-linux"), "cat", "/var/lib/wf-agent/" + marker], token));
            Assert.Equal(ppeState, StateHash("ppe"));
            Assert.Equal(devState, StateHash("dev"));
            await Local("down", "dev", token);
            Assert.Empty(await Resources("ps", "dev", token));
            Assert.Equal(4, (await Resources("volume", "dev", token)).Length);
            Assert.Equal(ppe, await HealthyMachine("ppe", token));
            output.WriteLine("Both actual CLI environments ready on isolated loopback bindings; login-free dashboards, " +
                "one engine leader each, API excluded; scoped down retained all volumes, SQL/blob/agent markers, " +
                "agent UUID and runtime state, with a fresh agent session after restart.");
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var failures = new List<Exception>();
            foreach (var environment in started)
            {
                try
                {
                    if (File.Exists(Path.Combine(repository, ".wf", "local", environment, "publish", "docker-compose.yaml")))
                        await Local("down", environment, cleanupDeadline.Token);
                    Assert.Empty(await Resources("ps", environment, cleanupDeadline.Token));
                    var owned = await Resources("volume", environment, cleanupDeadline.Token);
                    var allowed = new[] { "postgres-data", "blobs", "control-secrets", "agent-data" }
                        .Select(name => "kardia-" + environment + "_" + name).ToArray();
                    Assert.All(owned, volume => Assert.Contains(volume, allowed));
                    if (owned.Length != 0)
                        await Command("docker", ["volume", "rm", .. owned], cleanupDeadline.Token);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (failures.Count != 0) throw new AggregateException("Owned Compose cleanup failed.", failures);
        }

        async Task Local(string operation, string? environment, CancellationToken cancellation)
        {
            string[] arguments = environment is null
                ? [cli, "local", operation]
                : [cli, "local", operation, "--env", environment];
            await Command("dotnet", arguments, cancellation);
        }

        string StateHash(string environment) => Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(repository, ".wf", "local", environment, "runtime.env"))));

        async Task<Guid> HealthyMachine(string environment, CancellationToken cancellation)
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            ready.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                var inventory = await client.GetFromJsonAsync<JsonElement>(
                    $"http://{Address(environment)}:5000/api/v1/machines?status=Healthy", ready.Token);
                var machines = inventory.GetProperty("items").EnumerateArray().ToArray();
                if (machines.Length == 1)
                {
                    var machine = machines[0];
                    Assert.Equal("linux", machine.GetProperty("os").GetString());
                    Assert.Equal("compose-" + environment + "-linux", machine.GetProperty("name").GetString());
                    Assert.Equal(environment, machine.GetProperty("labels").GetProperty("environment").GetString());
                    return machine.GetProperty("id").GetGuid();
                }
                await Task.Delay(100, ready.Token);
            }
        }

        async Task<string[]> Resources(string type, string environment, CancellationToken cancellation)
        {
            var arguments = type == "ps" ? new[] { "ps", "-a" } : new[] { "volume", "ls" };
            var result = await Command("docker", [.. arguments, "--filter",
                "label=com.docker.compose.project=kardia-" + environment, "--format",
                type == "ps" ? "{{.Names}}" : "{{.Name}}"], cancellation);
            return result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        Task<string> Sql(string environment, string sql, CancellationToken cancellation) => Command("docker",
            ["exec", Container(environment, "postgres"), "sh", "-c",
                "export PGPASSWORD=\"$POSTGRES_PASSWORD\"; exec psql -U postgres -d workflow -v ON_ERROR_STOP=1 -Atc \"$1\"",
                "sh", sql], cancellation);
    }

    private static readonly string[] Environments = ["dev", "ppe"];
    private static string Address(string environment) => environment == "dev" ? "127.2.6.2" : "127.2.6.7";
    private static string Container(string environment, string service) => $"kardia-{environment}-{service}-1";

    private static async Task<string> Command(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Repository(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + executable);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var result = await stdout;
        var errors = await stderr;
        Assert.True(process.ExitCode == 0, $"{executable} exited {process.ExitCode}:\n{result}\n{errors}");
        return result.Trim();
    }

    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "infra", "local", "compose.override.yml")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Run the Compose acceptance test from this source checkout.");
    }
}
