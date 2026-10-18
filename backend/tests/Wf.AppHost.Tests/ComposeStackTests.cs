using System.Globalization;
using System.Net;
using System.Text.Json;
using static Wf.AppHost.Tests.ComposeGatewayTests;
using static Wf.AppHost.Tests.LocalStackScenarioTests;

namespace Wf.AppHost.Tests;

public sealed class ComposeStackTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Nightly")]
    public async Task Linux_scenario_A_survives_leader_kill_with_dev_and_ppe_running_beside_each_other()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_COMPOSE_SCENARIO") == "true",
            "Set WF_COMPOSE_SCENARIO=true on Linux with Docker, Aspire 13.6 and the normal Debug CLI. Both Compose projects must be unused.");
        Assert.True(OperatingSystem.IsLinux(), "The nightly Compose acceptance must execute on Linux.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(25));
        var token = deadline.Token;
        var repository = LocalStackModelTests.Repository();
        var cli = Path.Combine(repository, "backend", "src", "Wf.Cli", "bin", "Debug", "net10.0", "wf.dll");
        Assert.True(File.Exists(cli), "Build the normal Debug CLI before this acceptance test.");
        foreach (var environment in new[] { "dev", "ppe" })
        {
            Assert.Empty(await ProjectResources(environment, "ps", token));
            Assert.Empty(await ProjectResources(environment, "volume", token));
        }
        var root = Path.Combine(repository, ".agent-artifacts", "compose-scenario", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var started = new List<string>();
        using var dev = Client("127.2.6.2");
        using var ppe = Client("127.2.6.7");
        try
        {
            var team = "compose-" + Guid.NewGuid().ToString("N");
            var workflow = team + "/release-validation/scenario-a";
            var source = await CompileAndPackage(root, team, workflow, token, composeScenario: true);
            started.Add("dev");
            output.WriteLine(await Command("dotnet", [cli, "local", "up", "--env", "dev"], token));
            await Get(dev, "/healthz", token);
            var agent = await Agent(dev);
            await Register(dev, root, source, team, workflow, token);
            var accepted = await Post(dev, "/api/v1/runs", new
            {
                workflowId = workflow, version = "1.4.0", variables = new { env = "dev", version = "9.8.7" }
            }, HttpStatusCode.Accepted, token);
            var runId = accepted.GetProperty("runId").GetGuid();
            output.WriteLine("Scenario A accepted: " + runId);
            await Wait(async () => (await Get(dev, $"/api/v1/runs/{runId}/timeline", token))
                .GetProperty("attempts").EnumerateArray().Any(attempt =>
                    attempt.GetProperty("taskKey").GetString() == "build/compile" &&
                    attempt.GetProperty("startedAt").ValueKind != JsonValueKind.Null &&
                    attempt.GetProperty("endedAt").ValueKind == JsonValueKind.Null));
            var lease = (await Sql("SELECT leader_hint,epoch FROM leases")).Split('|');
            var leader = new Uri(lease[0]).Host;
            Assert.Contains(leader, new[] { "wf-engine-a", "wf-engine-b" });
            var killed = "kardia-dev-" + leader + "-1";
            await Command("docker", ["kill", killed], token);
            output.WriteLine($"Hard-killed {killed} at epoch {lease[1]} during build/compile.");
            started.Add("ppe");
            output.WriteLine(await Command("dotnet", [cli, "local", "up", "--env", "ppe"], token));
            await Wait(async () =>
            {
                var snapshot = await Get(dev, "/api/v1/runs/" + runId, token);
                var status = snapshot.GetProperty("status").GetInt32();
                Assert.True(status < 4, "Scenario A failed: " + snapshot);
                return status == 3;
            });
            var replacement = (await Sql("SELECT leader_hint,epoch FROM leases")).Split('|');
            Assert.NotEqual(lease[0], replacement[0]);
            Assert.True(long.Parse(replacement[1], CultureInfo.InvariantCulture) >
                long.Parse(lease[1], CultureInfo.InvariantCulture));
            Assert.Equal("exited", await Command("docker", ["inspect", "--format", "{{.State.Status}}", killed], token));
            await AssertExecution(dev, root, runId, agent, token, "/var/lib/wf-agent/packages", "dev");
            await Get(dev, "/healthz", token);
            await Get(ppe, "/healthz", token);
            await Get(dev, "/healthz/leader", token);
            await Get(ppe, "/healthz/leader", token);
            Assert.NotEqual(agent, await Agent(ppe));
            output.WriteLine($"Scenario A {runId} Succeeded: all eleven Linux attempts remained attempt 1, " +
                $"epoch {lease[1]} -> {replacement[1]}; distinct dev/PPE agents and both exact-IP gateways healthy.");
        }
        catch
        {
            using var diagnostics = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            foreach (var environment in started)
                foreach (var service in new[] { "wf-engine-a", "wf-engine-b", "wf-agent-linux", "wf-api", "wf-gateway" })
                    try
                    {
                        output.WriteLine(await Command("docker",
                            ["logs", "--tail", "100", $"kardia-{environment}-{service}-1"], diagnostics.Token));
                    }
                    catch (Exception error) { output.WriteLine($"Diagnostics {environment}/{service}: {error.Message}"); }
            throw;
        }
        finally
        {
            var failures = new List<Exception>();
            foreach (var environment in started.AsEnumerable().Reverse())
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try { await CleanupProject(cli, environment, cleanup.Token); }
                catch (Exception error)
                {
                    output.WriteLine($"Cleanup failed for owned kardia-{environment}: {error}");
                    failures.Add(error);
                }
            }
            Directory.Delete(root, recursive: true);
            if (failures.Count != 0) throw new AggregateException("Compose acceptance cleanup failed.", failures);
        }

        async Task<Guid> Agent(HttpClient client)
        {
            JsonElement machine = default;
            await Wait(async () =>
            {
                var items = (await Get(client, "/api/v1/machines?status=Healthy", token)).GetProperty("items");
                if (items.GetArrayLength() != 1) return false;
                machine = items[0];
                return true;
            });
            Assert.Equal("container", machine.GetProperty("labels").GetProperty("runtime").GetString());
            return machine.GetProperty("id").GetGuid();
        }

        Task<string> Sql(string sql) => Command("docker",
            ["exec", "kardia-dev-postgres-1", "sh", "-c",
                "export PGPASSWORD=\"$POSTGRES_PASSWORD\"; exec psql -U postgres -d workflow -v ON_ERROR_STOP=1 -Atc \"$1\"",
                "sh", sql], token);

        async Task Wait(Func<Task<bool>> condition)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromMinutes(2));
            while (!await condition()) await Task.Delay(200, wait.Token);
        }
    }

    private static HttpClient Client(string address) => new(new SocketsHttpHandler { UseProxy = false })
    {
        BaseAddress = new Uri("http://" + address), Timeout = TimeSpan.FromSeconds(10)
    };
}
