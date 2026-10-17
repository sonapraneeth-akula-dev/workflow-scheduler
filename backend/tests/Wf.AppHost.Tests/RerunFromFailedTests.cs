using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Wf.AppHost.Tests;

public sealed partial class LocalStackScenarioTests
{
    [Fact]
    [Trait("Category", "LocalStackRerun")]
    public Task Scenario_A_rerun_from_failed_US_reuses_real_outputs_then_executes_remaining_tasks_and_browser_lineage() =>
        ExecuteScenario(rerun: true);

    private static string RerunWorkflow(string root, string workflow) => $$"""
        #:project {{Path.Combine(Repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")}}
        #:property PublishAot=false
        #:property AssemblyName=ScenarioA.Rerun.Acceptance
        using Wf.Sdk;
        static void Stamp(TaskContext ctx, string stage)
        {
            ctx.Outputs["stage"] = stage;
            ctx.Outputs["pid"] = Environment.ProcessId;
            ctx.Outputs["assembly"] = System.Reflection.Assembly.GetExecutingAssembly().Location;
            ctx.Outputs["env"] = ctx.Var<string>("env");
            ctx.Outputs["version"] = ctx.Var<string>("version");
        }
        static void Smoke(TaskContext ctx, string probe)
        {
            Stamp(ctx, "Smoke-" + probe);
            ctx.Outputs["joined"] = ctx.Input<string>("deploy-eu", "receipt") + "|" + ctx.Input<string>("deploy-us", "receipt");
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
                .Task("deploy", ctx => {
                    Stamp(ctx, "Deploy-eu");
                    ctx.Outputs["receipt"] = "eu:" + ctx.Input<string>("build", "artifact");
                }))
            .Node("deploy-us", n => n.DependsOn("build").Input("build", "artifact").NoAutoRetry()
                .Task("deploy", async ctx => {
                    while (!File.Exists({{JsonSerializer.Serialize(Path.Combine(root, "release-us"))}}))
                        await Task.Delay(20, ctx.CancellationToken);
                    var failed = {{JsonSerializer.Serialize(Path.Combine(root, "us-failed-once"))}};
                    if (!File.Exists(failed)) {
                        await File.WriteAllTextAsync(failed, "First deployment failed", ctx.CancellationToken);
                        throw new InvalidOperationException("Scenario A original US deployment failed.");
                    }
                    Stamp(ctx, "Deploy-us");
                    ctx.Outputs["receipt"] = "us:" + ctx.Input<string>("build", "artifact");
                }))
            .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us")
                .Input("deploy-eu", "receipt").Input("deploy-us", "receipt").Parallel(3)
                .Task("api", ctx => Smoke(ctx, "api"))
                .Task("ui", ctx => Smoke(ctx, "ui"))
                .Task("perf-probe", ctx => Smoke(ctx, "perf-probe")))
            .PostProcess(ctx => Stamp(ctx, "PostProcess"))
            .SystemPostProcess(ctx => Stamp(ctx, "SystemPostProcess")));
        return await workflow.RunAsync(args);
        """;

    private async Task AssertRerunExecution(HttpClient client, string root, Guid parent, Guid agentId,
        JsonElement original, string connectionString, Uri ui, CancellationToken token)
    {
        Assert.Equal(5, original.GetProperty("nodes").GetProperty("build").GetInt32());
        Assert.Equal(5, original.GetProperty("nodes").GetProperty("deploy-eu").GetInt32());
        Assert.Equal(7, original.GetProperty("nodes").GetProperty("deploy-us").GetInt32());
        Assert.Equal(10, original.GetProperty("nodes").GetProperty("smoke").GetInt32());
        var version = original.GetProperty("versionId").GetGuid();
        var build = await Outputs(parent, "build/compile");
        var eu = await Outputs(parent, "deploy-eu/deploy");
        Assert.Equal("ppe:9.8.7", build.GetProperty("artifact").GetString());
        Assert.Equal("eu:ppe:9.8.7", eu.GetProperty("receipt").GetString());
        var failedTimeline = await Get(client, $"/api/v1/runs/{parent}/timeline", token);
        var failedUs = Assert.Single(failedTimeline.GetProperty("attempts").EnumerateArray(),
            attempt => attempt.GetProperty("taskKey").GetString() == "deploy-us/deploy");
        Assert.Equal("Failed", failedUs.GetProperty("terminalStatus").GetString());
        Assert.Equal(1, failedUs.GetProperty("attempt").GetInt32());

        var accepted = await Post(client, $"/api/v1/runs/{parent}/rerun", new { },
            HttpStatusCode.Accepted, token);
        var apiChild = accepted.GetProperty("runId").GetGuid();
        await WaitForSucceeded(apiChild);
        await CheckChild(apiChild);

        var frontend = Path.Combine(Repository, "frontend");
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "x", "--no-install", "playwright", "test", "rerun-execution.spec.ts" })
            start.ArgumentList.Add(argument);
        start.Environment["WF_RERUN_EXECUTION_ORIGIN"] = ui.AbsoluteUri.TrimEnd('/');
        start.Environment["WF_RERUN_EXECUTION_FIXTURE"] = JsonSerializer.Serialize(new
        {
            source = parent, workflowId = original.GetProperty("workflowId").GetString(), version, build, eu
        });
        using (var process = Process.Start(start)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                await process.WaitForExitAsync(deadline.Token);
                output.WriteLine(await stdout);
                output.WriteLine(await stderr);
                Assert.True(process.ExitCode == 0, $"Real rerun execution browsers failed ({process.ExitCode}).");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
            }
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        var children = new List<Guid>();
        await using (var read = new NpgsqlCommand("SELECT id FROM runs WHERE parent_run_id=$1 ORDER BY id", connection))
        {
            read.Parameters.AddWithValue(parent);
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) children.Add(reader.GetGuid(0));
        }
        Assert.Equal(4, children.Count);
        Assert.Contains(apiChild, children);
        foreach (var child in children)
        {
            if (child != apiChild) await CheckChild(child);
            await using var lineage = new NpgsqlCommand("""
                SELECT n.node_key,n.outputs=o.outputs,n.reused_from_run_id,n.status
                FROM node_runs n JOIN node_runs o ON o.run_id=$2 AND o.node_key=n.node_key
                WHERE n.run_id=$1 AND n.reused_from_run_id IS NOT NULL ORDER BY n.node_key
                """, connection);
            lineage.Parameters.AddWithValue(child);
            lineage.Parameters.AddWithValue(parent);
            var keys = new List<string>();
            await using var reader = await lineage.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                keys.Add(reader.GetString(0));
                Assert.True(reader.GetBoolean(1));
                Assert.Equal(parent, reader.GetGuid(2));
                Assert.Equal(11, reader.GetInt16(3));
            }
            Assert.Equal(["build", "deploy-eu"], keys);
        }
        Assert.Equal(build.GetRawText(), (await Outputs(parent, "build/compile")).GetRawText());
        Assert.Equal(eu.GetRawText(), (await Outputs(parent, "deploy-eu/deploy")).GetRawText());
        output.WriteLine($"Scenario A {parent}: one API and three browser reruns reused exact original Build/EU outputs; eight remaining real attempts per rerun succeeded.");

        async Task<JsonElement> Outputs(Guid run, string key) =>
            (await Get(client, $"/api/v1/runs/{run}/outputs?taskKey={Uri.EscapeDataString(key)}&attempt=1", token))
                .GetProperty("outputs");

        async Task WaitForSucceeded(Guid child)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                var snapshot = await Get(client, $"/api/v1/runs/{child}", deadline.Token);
                if (snapshot.GetProperty("status").GetInt32() >= 3)
                {
                    Assert.Equal(3, snapshot.GetProperty("status").GetInt32());
                    return;
                }
                await Task.Delay(50, deadline.Token);
            }
        }

        async Task CheckChild(Guid child)
        {
            var snapshot = await Get(client, $"/api/v1/runs/{child}", token);
            Assert.Equal(3, snapshot.GetProperty("status").GetInt32());
            Assert.Equal(parent, snapshot.GetProperty("parentRunId").GetGuid());
            Assert.Equal(version, snapshot.GetProperty("versionId").GetGuid());
            Assert.Equal(parent, snapshot.GetProperty("reusedNodes").GetProperty("build").GetGuid());
            Assert.Equal(parent, snapshot.GetProperty("reusedNodes").GetProperty("deploy-eu").GetGuid());
            Assert.All(snapshot.GetProperty("nodes").EnumerateObject(), node =>
                Assert.Equal(node.Name is "build" or "deploy-eu" ? 11 : 5, node.Value.GetInt32()));
            var timeline = await Get(client, $"/api/v1/runs/{child}/timeline", token);
            var attempts = timeline.GetProperty("attempts").EnumerateArray().ToArray();
            Assert.Equal(["$init/run", "$pre/run", "deploy-us/deploy", "smoke/api", "smoke/ui",
                "smoke/perf-probe", "$post/run", "$sys-post/run"], attempts.Select(attempt => attempt.GetProperty("taskKey").GetString()));
            foreach (var attempt in attempts)
            {
                Assert.Equal(agentId, attempt.GetProperty("agentId").GetGuid());
                Assert.Equal(1, attempt.GetProperty("attempt").GetInt32());
                Assert.Equal("Succeeded", attempt.GetProperty("terminalStatus").GetString());
                Assert.NotEqual(JsonValueKind.Null, attempt.GetProperty("startedAt").ValueKind);
                Assert.NotEqual(JsonValueKind.Null, attempt.GetProperty("endedAt").ValueKind);
                var key = attempt.GetProperty("taskKey").GetString()!;
                var result = await Outputs(child, key);
                Assert.True(result.GetProperty("pid").GetInt32() > 0);
                Assert.NotEqual(Environment.ProcessId, result.GetProperty("pid").GetInt32());
                Assert.StartsWith(Path.Combine(root, "agents", "local", "packages") + Path.DirectorySeparatorChar,
                    result.GetProperty("assembly").GetString()!, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                Assert.Equal("ppe", result.GetProperty("env").GetString());
                Assert.Equal("9.8.7", result.GetProperty("version").GetString());
                if (key == "deploy-us/deploy") Assert.Equal("us:ppe:9.8.7", result.GetProperty("receipt").GetString());
                if (key.StartsWith("smoke/", StringComparison.Ordinal))
                    Assert.Equal("eu:ppe:9.8.7|us:ppe:9.8.7", result.GetProperty("joined").GetString());
            }
            using var missing = await client.GetAsync($"/api/v1/runs/{child}/outputs?taskKey=build%2Fcompile&attempt=1", token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }
}
