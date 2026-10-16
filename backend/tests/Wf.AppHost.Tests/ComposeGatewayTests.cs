using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Wf.AppHost.Tests.LocalStackScenarioTests;

namespace Wf.AppHost.Tests;

public sealed class ComposeGatewayTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "ComposeGateway")]
    public async Task Real_Caddy_SPA_and_h2c_agent_finish_an_inflight_run_after_the_leader_is_killed()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_COMPOSE_GATEWAY") == "true",
            "Set WF_COMPOSE_GATEWAY=true with Aspire 13.6, Docker, Debug CLI and frontend browser dependencies. kardia-ppe must be unused.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        var repository = LocalStackModelTests.Repository();
        var cli = Path.Combine(repository, "backend", "src", "Wf.Cli", "bin", "Debug", "net10.0", "wf.dll");
        Assert.True(File.Exists(cli), "Build the normal Debug CLI before running this acceptance test.");
        Assert.Empty(await Resources("ps", token));
        Assert.Empty(await Resources("volume", token));
        var root = Path.Combine(repository, ".agent-artifacts", "compose-gateway", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri("http://127.2.6.7"), Timeout = TimeSpan.FromSeconds(10)
        };
        var started = false;
        try
        {
            var team = "gateway-" + Guid.NewGuid().ToString("N");
            var workflow = team + "/release-validation/failover";
            var source = await CompileAndPackage(root, team, workflow, token, $$"""
                #:project {{Path.Combine(repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")}}
                #:property PublishAot=false
                #:property AssemblyName=ScenarioA.Acceptance
                using Wf.Sdk;
                var workflow = Workflow.Define("{{workflow}}", "1.4.0", w => w
                    .Type("release-validation")
                    .SystemInit(ctx => ctx.Outputs["stage"] = "init")
                    .Node("work", n => n.Series()
                        .Task("held", async ctx => {
                            await Task.Delay(TimeSpan.FromSeconds(30), ctx.CancellationToken);
                            ctx.Outputs["pid"] = Environment.ProcessId;
                            ctx.Outputs["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                            ctx.Outputs["isLinux"] = OperatingSystem.IsLinux();
                            ctx.Outputs["receipt"] = "inflight-completed";
                        })
                        .Task("after", async ctx => {
                            ctx.Outputs["receipt"] = "standby-dispatched";
                            await ctx.Reports.PublishAsync("html", "Gateway report",
                                "<!doctype html><p>Gateway iframe ready</p>"u8.ToArray(), ctx.CancellationToken);
                        }))
                    .PostProcess(ctx => ctx.Outputs["stage"] = "post")
                    .SystemPostProcess(ctx => ctx.Outputs["stage"] = "system-post"));
                return await workflow.RunAsync(args);
                """);
            started = true;
            await Command("dotnet", [cli, "local", "up"], token);
            await Get(client, "/healthz", token);
            await Wait(async () =>
            {
                using var response = await client.GetAsync("/healthz/leader", token);
                return response.StatusCode == HttpStatusCode.OK;
            }, token);
            using (var page = await client.GetAsync("/runs/not-a-real-run", token))
            {
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync(token);
                Assert.Contains("<div id=\"root\">", html);
                Assert.Equal("DENY", Assert.Single(page.Headers.GetValues("X-Frame-Options")));
                Assert.Equal("nosniff", Assert.Single(page.Headers.GetValues("X-Content-Type-Options")));
                Assert.True(page.Headers.Contains("Content-Security-Policy-Report-Only"));
                Assert.True(page.Headers.Contains("Permissions-Policy"));
                var asset = Regex.Match(html, "src=\"(/assets/[^\"]+\\.js)\"").Groups[1].Value;
                Assert.NotEmpty(asset);
                using var script = await client.GetAsync(asset, token);
                Assert.Equal(HttpStatusCode.OK, script.StatusCode);
                Assert.Equal(TimeSpan.FromDays(365), script.Headers.CacheControl?.MaxAge);
                Assert.Contains(script.Headers.CacheControl!.Extensions, extension => extension.Name == "immutable");
            }
            foreach (var path in new[] { "/metrics", "/openapi/v1.json", "/swagger/index.html" })
            {
                using var ops = await client.GetAsync(path, token);
                Assert.Equal(HttpStatusCode.NotFound, ops.StatusCode);
            }
            using (var index = await client.GetAsync("/index.html", token))
                Assert.True(index.Headers.CacheControl?.NoStore);
            using (var missingApi = await client.GetAsync("/api/v1/does-not-exist", token))
            {
                Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
                Assert.DoesNotContain("<div id=\"root\">", await missingApi.Content.ReadAsStringAsync(token));
            }
            await Wait(async () => (await Get(client, "/api/v1/machines?status=Healthy", token))
                .GetProperty("items").GetArrayLength() == 1, token);
            await Register(client, root, source, team, workflow, token);
            var accepted = await Post(client, "/api/v1/runs", new { workflowId = workflow, version = "1.4.0" },
                HttpStatusCode.Accepted, token);
            var runId = accepted.GetProperty("runId").GetGuid();
            await Wait(async () =>
            {
                var timeline = await Get(client, $"/api/v1/runs/{runId}/timeline", token);
                return timeline.GetProperty("attempts").EnumerateArray().Any(attempt =>
                    attempt.GetProperty("taskKey").GetString() == "work/held" &&
                    attempt.GetProperty("startedAt").ValueKind != JsonValueKind.Null &&
                    attempt.GetProperty("endedAt").ValueKind == JsonValueKind.Null);
            }, token);
            var lease = (await Sql("SELECT leader_hint,epoch FROM leases", token)).Split('|');
            var leader = new Uri(lease[0]).Host;
            Assert.Contains(leader, new[] { "wf-engine-a", "wf-engine-b" });
            var clock = Stopwatch.StartNew();
            await Command("docker", ["kill", Container(leader)], token);
            await Wait(async () =>
            {
                var snapshot = await Get(client, "/api/v1/runs/" + runId, token);
                var status = snapshot.GetProperty("status").GetInt32();
                Assert.True(status < 4, "Gateway run failed: " + snapshot);
                return status == 3;
            }, token);
            var replacement = (await Sql("SELECT leader_hint,epoch FROM leases", token)).Split('|');
            Assert.NotEqual(lease[0], replacement[0]);
            Assert.True(long.Parse(replacement[1], System.Globalization.CultureInfo.InvariantCulture) >
                long.Parse(lease[1], System.Globalization.CultureInfo.InvariantCulture));
            var completed = await Get(client, $"/api/v1/runs/{runId}/timeline", token);
            Assert.All(completed.GetProperty("attempts").EnumerateArray(), attempt =>
            {
                Assert.Equal("Succeeded", attempt.GetProperty("terminalStatus").GetString());
                Assert.Equal(1, attempt.GetProperty("attempt").GetInt32());
            });
            foreach (var (task, receipt) in new[]
                     { ("work/held", "inflight-completed"), ("work/after", "standby-dispatched") })
            {
                var result = await Get(client,
                    $"/api/v1/runs/{runId}/outputs?taskKey={Uri.EscapeDataString(task)}&attempt=1", token);
                Assert.Equal(receipt, result.GetProperty("outputs").GetProperty("receipt").GetString());
                if (task == "work/held")
                {
                    Assert.True(result.GetProperty("outputs").GetProperty("isLinux").GetBoolean());
                    Assert.True(result.GetProperty("outputs").GetProperty("pid").GetInt32() > 0);
                }
            }
            await Get(client, "/healthz/leader", token);
            var reports = await Get(client, $"/api/v1/runs/{runId}/reports", token);
            var report = Assert.Single(reports.GetProperty("items").EnumerateArray());
            var reportId = report.GetProperty("id").GetGuid();
            using (var content = await client.GetAsync($"/api/v1/reports/{reportId}/content", token))
            {
                Assert.Equal(HttpStatusCode.OK, content.StatusCode);
                Assert.False(content.Headers.Contains("X-Frame-Options"));
                Assert.Contains("sandbox allow-scripts",
                    Assert.Single(content.Headers.GetValues("Content-Security-Policy")));
                Assert.True(content.Headers.CacheControl?.NoStore);
            }
            await Command("bun", ["run", "scripts/compose-gateway-browser.ts", runId.ToString(), reportId.ToString()], token,
                Path.Combine(repository, "frontend"));
            output.WriteLine($"Real gateway HTTP/API/SPA and three browser MessagePack WebSockets passed; " +
                $"Linux h2c agent retained attempt 1 across hard-kill of {leader}; standby completed run {runId} " +
                $"and dispatched the following task in {clock.Elapsed.TotalSeconds:F1}s after kill.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            if (started)
            {
                try
                {
                    await Command("dotnet", [cli, "local", "down"], cleanup.Token);
                    Assert.Empty(await Resources("ps", cleanup.Token));
                    var volumes = await Resources("volume", cleanup.Token);
                    var allowed = new[] { "postgres-data", "blobs", "control-secrets", "agent-data" }
                        .Select(name => "kardia-ppe_" + name).ToArray();
                    Assert.All(volumes, volume => Assert.Contains(volume, allowed));
                    if (volumes.Length != 0)
                        await Command("docker", ["volume", "rm", .. volumes], cleanup.Token);
                }
                catch
                {
                    output.WriteLine("Gateway acceptance cleanup failed; inspect only the kardia-ppe project.");
                    throw;
                }
            }
            Directory.Delete(root, recursive: true);
        }

        Task<string> Sql(string sql, CancellationToken cancellation) => Command("docker",
            ["exec", Container("postgres"), "sh", "-c",
                "export PGPASSWORD=\"$POSTGRES_PASSWORD\"; exec psql -U postgres -d workflow -v ON_ERROR_STOP=1 -Atc \"$1\"",
                "sh", sql], cancellation);

        async Task<string[]> Resources(string type, CancellationToken cancellation)
        {
            var args = type == "ps" ? new[] { "ps", "-a" } : new[] { "volume", "ls" };
            var result = await Command("docker", [.. args, "--filter",
                "label=com.docker.compose.project=kardia-ppe", "--format",
                type == "ps" ? "{{.Names}}" : "{{.Name}}"], cancellation);
            return result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    private static string Container(string service) => "kardia-ppe-" + service + "-1";

    private static async Task Wait(Func<Task<bool>> condition, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        while (!await condition()) await Task.Delay(100, deadline.Token);
    }

    private static async Task<string> Command(string executable, string[] arguments, CancellationToken token,
        string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory ?? LocalStackModelTests.Repository(),
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
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
}
