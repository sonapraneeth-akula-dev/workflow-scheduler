using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Npgsql;
using Wf.AppHost;

namespace Wf.AppHost.Tests;

public sealed class LocalStackStartupTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Three_native_and_one_actual_Linux_agent_are_healthy_over_HTTP_and_retain_distinct_state_after_restart()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_APPHOST_STARTUP") == "true",
            "Set WF_APPHOST_STARTUP=true to launch three native agents and an actual Linux container.");
        var root = Path.Combine(Path.GetTempPath(), "wf-apphost-agents-" + Guid.NewGuid().ToString("N"));
        var postgresVolume = "wf-apphost-agents-" + Guid.NewGuid().ToString("N");
        string? linuxVolume = null;
        var ports = FreePorts(3);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Dictionary<Guid, Guid>? previous = null;
        Directory.CreateDirectory(root);
        try
        {
            for (var iteration = 0; iteration < 2; iteration++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                var token = deadline.Token;
                var clock = Stopwatch.StartNew();
                await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(typeof(LocalStack),
                    new DistributedApplicationTestingBuilderOptions { EnableDashboard = true },
                    [
                        "--agents", "3", "--linux-agent", "--Local:DataRoot=" + root,
                        "--Local:PostgresVolume=" + postgresVolume, "--Local:ApiPort=" + ports[0],
                        "--Local:EnginePort=" + ports[1], "--Local:UiPort=" + ports[2],
                        "--Parameters:storage-key=" + key, "--Parameters:postgres-password=" + password
                    ], token);
                var linux = Assert.Single(builder.Resources.OfType<ContainerResource>(), resource => resource.Name == "wf-agent-linux");
                var volume = Assert.Single(linux.Annotations.OfType<ContainerMountAnnotation>()).Source;
                Assert.NotNull(volume);
                Assert.Equal(linuxVolume ?? volume, volume);
                linuxVolume = volume;
                await using var app = await builder.BuildAsync(token);
                await app.StartAsync(token);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("wf-host", token);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("wf-ui", token);
                using var client = new HttpClient { BaseAddress = app.GetEndpoint("wf-host", "api"), Timeout = TimeSpan.FromSeconds(10) };
                JsonElement[] machines;
                while (true)
                {
                    using var response = await client.GetAsync("/api/v1/machines?status=Healthy", token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    machines = document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
                    if (machines.Length == 4) break;
                    await Task.Delay(100, token);
                }
                Assert.Equal(4, machines.Select(machine => machine.GetProperty("name").GetString()).Distinct().Count());
                Assert.Equal(4, machines.Select(machine => machine.GetProperty("id").GetGuid()).Distinct().Count());
                Assert.Equal(["wf-agent", "wf-agent-2", "wf-agent-3", "wf-agent-linux"],
                    machines.Select(machine => machine.GetProperty("labels").GetProperty("instance").GetString()).Order());
                var linuxMachine = Assert.Single(machines, machine =>
                    machine.GetProperty("labels").GetProperty("runtime").GetString() == "container");
                Assert.Equal("linux", linuxMachine.GetProperty("os").GetString());
                Assert.Equal(3, machines.Count(machine => machine.GetProperty("labels").GetProperty("runtime").GetString() == "native"));
                foreach (var (directory, resource) in new[]
                {
                    ("local", "wf-agent"), ("local-2", "wf-agent-2"), ("local-3", "wf-agent-3"), ("linux", "wf-agent-linux")
                })
                {
                    var identity = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(root, "agents", directory, "id"), token));
                    Assert.Equal(identity, Assert.Single(machines, machine =>
                        machine.GetProperty("labels").GetProperty("instance").GetString() == resource).GetProperty("id").GetGuid());
                    if (directory == "linux") continue;
                    foreach (var leaf in new[] { "results", "packages" })
                        Assert.True(Directory.Exists(Path.Combine(root, "agents", directory, leaf)), $"{resource}/{leaf} missing.");
                }
                var connectionString = await app.GetConnectionStringAsync("wf", token);
                await using var database = NpgsqlDataSource.Create(connectionString!);
                var sessions = new Dictionary<Guid, Guid>();
                await using (var query = database.CreateCommand("SELECT id,session_id FROM agents"))
                await using (var reader = await query.ExecuteReaderAsync(token))
                    while (await reader.ReadAsync(token)) sessions.Add(reader.GetGuid(0), reader.GetGuid(1));
                Assert.Equal(4, sessions.Count);
                if (previous is not null)
                {
                    Assert.Equal(previous.Keys.Order(), sessions.Keys.Order());
                    Assert.All(sessions, pair => Assert.NotEqual(previous[pair.Key], pair.Value));
                }
                previous = sessions;
                var container = (await Command("docker", ["ps", "--filter", "volume=" + linuxVolume, "--format", "{{.ID}}"])).Trim();
                Assert.Matches("^[a-f0-9]{12}$", container);
                Assert.Equal("700", (await Command("docker", ["exec", container, "stat", "-c", "%a", "/var/lib/wf-agent"])).Trim());
                Assert.NotEqual("0", (await Command("docker", ["exec", container, "id", "-u"])).Trim());
                if (iteration == 0)
                    await Command("docker", ["exec", container, "sh", "-c", "printf persisted > /var/lib/wf-agent/startup-probe"]);
                else
                    Assert.Equal("persisted", await Command("docker", ["exec", container, "cat", "/var/lib/wf-agent/startup-probe"]));
                output.WriteLine($"Mixed agents start {iteration + 1}: GET /api/v1/machines lists four Healthy distinct agents, " +
                    $"three native + one nonroot Linux container, in {clock.Elapsed.TotalSeconds:F1}s. UUIDs/volume retained, sessions replaced.");
                await app.StopAsync(token);
            }
        }
        finally
        {
            foreach (var volume in new[] { linuxVolume, postgresVolume }.OfType<string>())
            {
                var exists = await Command("docker", ["volume", "ls", "--filter", "name=^" + volume + "$", "--format", "{{.Name}}"]);
                if (exists.Trim() == volume) await Command("docker", ["volume", "rm", volume]);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_stack_becomes_healthy_with_dashboard_and_preserves_data_across_restart(bool externalDatabase)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_APPHOST_STARTUP") == "true",
            "Set WF_APPHOST_STARTUP=true to launch the actual AppHost, PostgreSQL, dashboard, agent and Bun.");
        var postgresBin = Environment.GetEnvironmentVariable("WF_NATIVE_POSTGRES_BIN");
        if (externalDatabase)
            Assert.SkipUnless(postgresBin is not null, "Set WF_NATIVE_POSTGRES_BIN to a native PostgreSQL bin directory.");
        var root = Path.Combine(Path.GetTempPath(), "wf-apphost-startup-" + Guid.NewGuid().ToString("N"));
        var volume = "wf-apphost-test-" + Guid.NewGuid().ToString("N");
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var ports = FreePorts(4);
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "postgres");
        var postgresStarted = false;
        try
        {
            string? external = null;
            if (externalDatabase)
            {
                await Command(Path.Combine(postgresBin!, Executable("initdb")), ["-D", data, "-A", "trust", "-U", "postgres"]);
                postgresStarted = true;
                await Command(Path.Combine(postgresBin!, Executable("pg_ctl")),
                    ["-D", data, "-l", Path.Combine(root, "postgres.log"), "-w", "-t", "30",
                        "-o", $"-h 127.0.0.1 -p {ports[3]} -F", "start"], capture: false);
                external = $"Host=127.0.0.1;Port={ports[3]};Database=postgres;Username=postgres;Pooling=false";
            }

            Guid? identity = null;
            Guid? session = null;
            var blob = Path.Combine(root, "blobs", "startup-persistence-probe");
            for (var iteration = 0; iteration < 2; iteration++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(120));
                var token = deadline.Token;
                var clock = Stopwatch.StartNew();
                var arguments = new List<string>
                {
                    "--Local:DataRoot=" + root, "--Local:PostgresVolume=" + volume,
                    "--Local:ApiPort=" + ports[0], "--Local:EnginePort=" + ports[1], "--Local:UiPort=" + ports[2],
                    "--Parameters:storage-key=" + key, "--Parameters:postgres-password=" + password
                };
                if (external is not null) arguments.Add("--ConnectionStrings:wf=" + external);
                await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(typeof(LocalStack),
                    new DistributedApplicationTestingBuilderOptions { EnableDashboard = true }, arguments.ToArray(), token);
                if (externalDatabase) Assert.Empty(builder.Resources.OfType<ContainerResource>());
                await using var app = await builder.BuildAsync(token);
                await app.StartAsync(token);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("wf-host", token);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("wf-ui", token);
                var connectionString = await app.GetConnectionStringAsync("wf", token);
                Assert.NotNull(connectionString);
                await using var database = NpgsqlDataSource.Create(connectionString);
                var currentIdentity = Guid.Parse(File.ReadAllText(Path.Combine(root, "agents", "local", "id")));
                var currentSession = await HealthyAgent(database, currentIdentity, token);
                using var client = new HttpClient();
                using var readiness = await client.GetAsync(app.GetEndpoint("wf-host", "api") + "healthz", token);
                Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
                using var health = await client.GetAsync(app.GetEndpoint("wf-host", "api") + "healthz/leader", token);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                using var proxy = await client.GetAsync(app.GetEndpoint("wf-ui") + "api/v1/workflows", token);
                Assert.Equal(HttpStatusCode.OK, proxy.StatusCode);
                Assert.Equal("application/json", proxy.Content.Headers.ContentType?.MediaType);
                using var page = await client.GetAsync(app.GetEndpoint("wf-ui"), token);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains("src/main.tsx", await page.Content.ReadAsStringAsync(token));
                var dashboard = await app.GetDashboardUrlAsync(token);
                using var dashboardPage = await client.GetAsync(dashboard, token);
                Assert.Equal(HttpStatusCode.OK, dashboardPage.StatusCode);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120));
                output.WriteLine($"{(externalDatabase ? "native PostgreSQL" : "Docker PostgreSQL")} start {iteration + 1}: " +
                    $"dashboard, API, UI proxy and real agent healthy in {clock.Elapsed.TotalSeconds:F1}s.");

                if (iteration == 0)
                {
                    identity = currentIdentity;
                    session = currentSession;
                    await using var marker = database.CreateCommand(
                        "CREATE TABLE apphost_persistence_probe(value text NOT NULL); INSERT INTO apphost_persistence_probe VALUES ('survived');");
                    await marker.ExecuteNonQueryAsync(token);
                    Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                    await File.WriteAllTextAsync(blob, "persisted local blob", token);
                }
                else
                {
                    Assert.Equal(identity, currentIdentity);
                    Assert.NotEqual(session, currentSession);
                    await using var marker = database.CreateCommand("SELECT value FROM apphost_persistence_probe");
                    Assert.Equal("survived", await marker.ExecuteScalarAsync(token));
                    Assert.Equal("persisted local blob", await File.ReadAllTextAsync(blob, token));
                }
                await app.StopAsync(token);
            }
        }
        finally
        {
            if (postgresStarted && File.Exists(Path.Combine(data, "postmaster.pid")))
                await Command(Path.Combine(postgresBin!, Executable("pg_ctl")), ["-D", data, "-m", "fast", "-w", "stop"], capture: false);
            if (!externalDatabase)
            {
                var exists = await Command("docker", ["volume", "ls", "--filter", "name=^" + volume + "$", "--format", "{{.Name}}"]);
                if (exists.Trim() == volume) await Command("docker", ["volume", "rm", volume]);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Guid> HealthyAgent(NpgsqlDataSource database, Guid id, CancellationToken token)
    {
        while (true)
        {
            await using var command = database.CreateCommand("SELECT session_id FROM agents WHERE id = $1 AND status = 0");
            command.Parameters.AddWithValue(id);
            if (await command.ExecuteScalarAsync(token) is Guid session) return session;
            await Task.Delay(100, token);
        }
    }

    private static int[] FreePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }
            return listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var listener in listeners) listener.Stop();
        }
    }

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static async Task<string> Command(string executable, string[] arguments, bool capture = true)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + executable);
        var stdout = capture ? process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken) : Task.FromResult("");
        var stderr = capture ? process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken) : Task.FromResult("");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var text = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {text}\n{error}");
        return text;
    }
}
