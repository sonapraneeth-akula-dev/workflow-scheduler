using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wf.AppHost;

namespace Wf.AppHost.Tests;

public sealed class LocalStackScenarioTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "LocalStackScenario")]
    public async Task Scenario_A_executes_through_real_AppHost_HTTP_registration_dispatch_and_agent_task_hosts()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_APPHOST_SCENARIO") == "true",
            "Set WF_APPHOST_SCENARIO=true and WF_TEST_POSTGRES to run the full local-stack acceptance test.");
        var adminConnection = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("WF_TEST_POSTGRES") ??
            throw new InvalidOperationException("WF_TEST_POSTGRES must identify a PostgreSQL administrator database."))
        { Pooling = false };
        var id = Guid.NewGuid().ToString("N");
        var databaseName = "wf_scenario_" + id;
        var root = Path.Combine(Repository, ".agent-artifacts", "apphost-scenario", id);
        await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(8));
            var token = deadline.Token;
            var team = "scenario-" + id;
            var workflow = team + "/release-validation/scenario-a";
            var source = await CompileAndPackage(root, team, workflow, token);
            var connection = new NpgsqlConnectionStringBuilder(adminConnection.ConnectionString) { Database = databaseName };
            var ports = FreePorts();
            using var startupDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            startupDeadline.CancelAfter(TimeSpan.FromSeconds(120));
            var startupToken = startupDeadline.Token;
            var clock = Stopwatch.StartNew();
            await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(typeof(LocalStack),
                new DistributedApplicationTestingBuilderOptions { EnableDashboard = true },
                [
                    "--Local:DataRoot=" + root, "--Local:ApiPort=" + ports[0],
                    "--Local:EnginePort=" + ports[1], "--Local:UiPort=" + ports[2],
                    "--ConnectionStrings:wf=" + connection.ConnectionString,
                    "--Parameters:storage-key=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
                ], startupToken);
            Assert.Empty(builder.Resources.OfType<ContainerResource>());
            await using var app = await builder.BuildAsync(startupToken);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                await app.StartAsync(startupToken);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("wf-host", startupToken);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("wf-ui", startupToken);
                client.BaseAddress = app.GetEndpoint("wf-host", "api");
                await Get(client, "/healthz", startupToken);
                await Get(client, "/healthz/leader", startupToken);
                using var page = await client.GetAsync(app.GetEndpoint("wf-ui"), startupToken);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains("src/main.tsx", await page.Content.ReadAsStringAsync(startupToken));
                using var proxy = await client.GetAsync(new Uri(app.GetEndpoint("wf-ui"), "api/v1/workflows"), startupToken);
                Assert.Equal(HttpStatusCode.OK, proxy.StatusCode);
                Assert.Equal("application/json", proxy.Content.Headers.ContentType?.MediaType);
                JsonElement machine;
                while (true)
                {
                    var inventory = await Get(client, "/api/v1/machines?status=Healthy", startupToken);
                    var machines = inventory.GetProperty("items").EnumerateArray().ToArray();
                    if (machines.Length == 1) { machine = machines[0]; break; }
                    await Task.Delay(100, startupToken);
                }
                var agentId = machine.GetProperty("id").GetGuid();
                Assert.Equal("native", machine.GetProperty("labels").GetProperty("runtime").GetString());
                Assert.Equal(Guid.Parse(await File.ReadAllTextAsync(Path.Combine(root, "agents", "local", "id"), startupToken)),
                    agentId);
                var ready = clock.Elapsed;
                Assert.True(ready < TimeSpan.FromSeconds(120), $"Stack startup took {ready}.");
                output.WriteLine($"Real PostgreSQL, control plane, native agent and Vite ready in {ready.TotalSeconds:F1}s.");

                await Register(client, root, source, team, workflow, token);
                var accepted = await Post(client, "/api/v1/runs",
                    new { workflowId = workflow, version = "1.4.0", variables = new { env = "ppe", version = "9.8.7" } },
                    HttpStatusCode.Accepted, token);
                var runId = accepted.GetProperty("runId").GetGuid();
                output.WriteLine("Accepted public run: " + runId);
                using var runDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                runDeadline.CancelAfter(TimeSpan.FromMinutes(2));
                JsonElement snapshot = default;
                try
                {
                    while (true)
                    {
                        snapshot = await Get(client, "/api/v1/runs/" + runId, runDeadline.Token);
                        var status = snapshot.GetProperty("status").GetInt32();
                        if (status >= 3)
                        {
                            Assert.True(status == 3, "Run did not succeed: " + snapshot);
                            break;
                        }
                        await Task.Delay(100, runDeadline.Token);
                    }
                }
                catch
                {
                    output.WriteLine("Last public run snapshot: " + snapshot);
                    throw;
                }
                Assert.Equal(workflow, snapshot.GetProperty("workflowId").GetString());
                Assert.All(snapshot.GetProperty("tasks").EnumerateObject(), task => Assert.Equal(5, task.Value.GetInt32()));
                await AssertExecution(client, root, runId, agentId, token);
                output.WriteLine($"Scenario A {runId}: full lifecycle, series command/function, diamond inputs and three parallel smoke tasks succeeded.");
            }
            catch
            {
                var logs = app.Services.GetRequiredService<ResourceLoggerService>();
                foreach (var resource in builder.Resources.Where(resource =>
                    resource.Name is "wf-migrations" or "wf-host" or "wf-agent" or "wf-ui"))
                    await foreach (var batch in logs.GetAllAsync(resource))
                        foreach (var line in batch) output.WriteLine($"{resource.Name}: {line.Content}");
                throw;
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await app.StopAsync(cleanup.Token);
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(cleanup.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CompileAndPackage(string root, string team, string workflow, CancellationToken token)
    {
        var sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var source = $$"""
            #:project {{Path.Combine(Repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")}}
            #:property PublishAot=false
            #:property AssemblyName=ScenarioA.Acceptance
            using Wf.Sdk;
            static void Stamp(TaskContext ctx, string stage)
            {
                ctx.Outputs["stage"] = stage;
                ctx.Outputs["pid"] = Environment.ProcessId;
                ctx.Outputs["assembly"] = System.Reflection.Assembly.GetExecutingAssembly().Location;
                ctx.Outputs["env"] = ctx.Var<string>("env");
                ctx.Outputs["version"] = ctx.Var<string>("version");
            }
            static void Deploy(TaskContext ctx, string region)
            {
                Stamp(ctx, "Deploy-" + region);
                ctx.Outputs["receipt"] = region + ":" + ctx.Input<string>("build", "artifact");
            }
            static void Smoke(TaskContext ctx, string probe)
            {
                Stamp(ctx, "Smoke-" + probe);
                ctx.Outputs["joined"] = ctx.Input<string>("deploy-eu", "receipt") + "|" +
                    ctx.Input<string>("deploy-us", "receipt");
            }
            var workflow = Workflow.Define("{{workflow}}", "1.4.0", w => w
                .Type("release-validation")
                .Variables(v => v.String("env", "dev", allowed: ["dev", "ppe"]).String("version", "0.0.0"))
                .SystemInit(ctx => Stamp(ctx, "SystemInit"))
                .PreSetup(ctx => Stamp(ctx, "PreSetup"))
                .Node("build", n => n.Series().Task("sdk", Tasks.Command("dotnet", "--version"))
                    .Task("compile", ctx => {
                        Stamp(ctx, "Build");
                        ctx.Outputs["artifact"] = ctx.Var<string>("env") + ":" + ctx.Var<string>("version");
                    }))
                .Node("deploy-eu", n => n.DependsOn("build").Input("build", "artifact")
                    .Task("deploy", ctx => Deploy(ctx, "eu")))
                .Node("deploy-us", n => n.DependsOn("build").Input("build", "artifact")
                    .Task("deploy", ctx => Deploy(ctx, "us")))
                .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us")
                    .Input("deploy-eu", "receipt").Input("deploy-us", "receipt").Parallel(maxParallel: 3)
                    .Task("api", ctx => Smoke(ctx, "api"))
                    .Task("ui", ctx => Smoke(ctx, "ui"))
                    .Task("perf-probe", ctx => Smoke(ctx, "perf-probe")))
                .PostProcess(ctx => Stamp(ctx, "PostProcess"))
                .SystemPostProcess(ctx => Stamp(ctx, "SystemPostProcess")));
            return await workflow.RunAsync(args);
            """;
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "workflow.cs"), source, new UTF8Encoding(false), token);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "team.json"),
            JsonSerializer.Serialize(Registry(team)), new UTF8Encoding(false), token);
        var cli = Path.Combine(Repository, "backend", "src", "Wf.Cli", "bin", "Debug", "net10.0", "wf.dll");
        Assert.True(File.Exists(cli), "Build backend\\src\\Wf.Cli in default Debug configuration before this acceptance test.");
        var arguments = new List<string>
        {
            cli, "validate", sourceDirectory, "--work-directory", Path.Combine(root, "work"),
            "--package", Path.Combine(root, "scenario-a.zip")
        };
        foreach (var name in new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" })
            arguments.AddRange(["--input", Path.Combine(Repository, "backend", "src", name)]);
        arguments.AddRange(["--input", Path.Combine(Repository, "backend", "proto")]);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Repository
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["WarningsNotAsErrors"] = "NU1902;NU1903";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the production workflow compiler.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(process.ExitCode == 0, $"Production CLI validation/package failed ({process.ExitCode}):\n{await stdout}\n{await stderr}");
        return source;
    }

    private static object Registry(string team) => new
    {
        team,
        types = new[] { new { team, name = "release-validation", description = "Full-stack scenario A", metadataSchema = new { keys = Array.Empty<object>() } } }
    };

    private static async Task Register(HttpClient client, string root, string source, string team, string workflow,
        CancellationToken token)
    {
        await Post(client, "/api/v1/registrations",
            new { gitCommit = new string('a', 40), gitAuthor = "Scenario A acceptance", registry = Registry(team) },
            HttpStatusCode.Created, token);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "scenario-a.zip"), token);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var grant = await Post(client, "/api/v1/upload-grants", new { kind = "package", sha256, size = bytes.LongLength },
            HttpStatusCode.Created, token);
        Assert.False(grant.GetProperty("exists").GetBoolean());
        using (var upload = await client.PutAsync(grant.GetProperty("uploadUri").GetString(), new ByteArrayContent(bytes), token))
            Assert.True(upload.StatusCode == HttpStatusCode.Created, await upload.Content.ReadAsStringAsync(token));
        var manifestPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "work", "c"), "manifest.json", SearchOption.AllDirectories));
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, token));
        await Post(client, "/api/v1/registrations", new
        {
            gitCommit = new string('b', 40), gitAuthor = "Scenario A acceptance", workflowId = workflow,
            manifest = manifest.RootElement, package = new { sha256, size = bytes.LongLength }, source
        }, HttpStatusCode.Created, token);
    }

    private static async Task AssertExecution(HttpClient client, string root, Guid runId, Guid agentId, CancellationToken token)
    {
        string[] keys = ["$init/run", "$pre/run", "build/sdk", "build/compile", "deploy-eu/deploy",
            "deploy-us/deploy", "smoke/api", "smoke/ui", "smoke/perf-probe", "$post/run", "$sys-post/run"];
        var timeline = await Get(client, $"/api/v1/runs/{runId}/timeline", token);
        Assert.Equal("Succeeded", timeline.GetProperty("status").GetString());
        var attempts = timeline.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(keys, attempts.Select(attempt => attempt.GetProperty("taskKey").GetString()));
        Assert.All(attempts, attempt =>
        {
            Assert.Equal(1, attempt.GetProperty("attempt").GetInt32());
            Assert.Equal("Succeeded", attempt.GetProperty("terminalStatus").GetString());
            Assert.Equal(agentId, attempt.GetProperty("agentId").GetGuid());
            Assert.NotEqual(JsonValueKind.Null, attempt.GetProperty("startedAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, attempt.GetProperty("endedAt").ValueKind);
        });
        var tasks = attempts.ToDictionary(attempt => attempt.GetProperty("taskKey").GetString()!);
        Before("$init/run", "$pre/run");
        Before("$pre/run", "build/sdk");
        Before("build/sdk", "build/compile");
        foreach (var branch in new[] { "deploy-eu/deploy", "deploy-us/deploy" })
        {
            Before("build/compile", branch);
            foreach (var smoke in new[] { "smoke/api", "smoke/ui", "smoke/perf-probe" }) Before(branch, smoke);
        }
        foreach (var smoke in new[] { "smoke/api", "smoke/ui", "smoke/perf-probe" }) Before(smoke, "$post/run");
        Before("$post/run", "$sys-post/run");
        foreach (var key in keys.Where(key => key != "build/sdk"))
        {
            var response = await Get(client, $"/api/v1/runs/{runId}/outputs?taskKey={Uri.EscapeDataString(key)}&attempt=1", token);
            var result = response.GetProperty("outputs");
            Assert.True(result.GetProperty("pid").GetInt32() > 0);
            Assert.NotEqual(Environment.ProcessId, result.GetProperty("pid").GetInt32());
            var assembly = result.GetProperty("assembly").GetString()!;
            Assert.True(assembly.StartsWith(Path.Combine(root, "agents", "local", "packages") + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), assembly);
            Assert.Equal("ppe", result.GetProperty("env").GetString());
            Assert.Equal("9.8.7", result.GetProperty("version").GetString());
            if (key == "build/compile") Assert.Equal("ppe:9.8.7", result.GetProperty("artifact").GetString());
            if (key.StartsWith("smoke/", StringComparison.Ordinal))
                Assert.Equal("eu:ppe:9.8.7|us:ppe:9.8.7", result.GetProperty("joined").GetString());
        }
        void Before(string first, string second) =>
            Assert.True(tasks[first].GetProperty("endedAt").GetDateTimeOffset() <=
                tasks[second].GetProperty("dispatchedAt").GetDateTimeOffset(), first + " must finish before " + second);
    }

    private static async Task<JsonElement> Post(HttpClient client, string path, object body, HttpStatusCode expected,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, body, token);
        var text = await response.Content.ReadAsStringAsync(token);
        Assert.True(response.StatusCode == expected, $"POST {path}: {(int)response.StatusCode} {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> Get(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        var text = await response.Content.ReadAsStringAsync(token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {text}");
        if (response.Content.Headers.ContentType?.MediaType != "application/json") return default;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static int[] FreePorts()
    {
        var listeners = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        try
        {
            foreach (var listener in listeners) listener.Start();
            return listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally { foreach (var listener in listeners) listener.Stop(); }
    }

    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "backend", "proto"))) return directory.FullName;
            throw new DirectoryNotFoundException("The local-stack acceptance test requires the repository source tree.");
        }
    }
}
