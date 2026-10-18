using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Wf.Contracts;
using Wf.Engine.Core;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Integration.Tests.Execution;

public sealed class LocalEngineTests
{
    [Fact]
    public async Task ScenarioAExecutesCommandHttpRetryBranchesJoinAndStagesWithMetadata()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = 0;
        app.MapGet("/deploy", () => Interlocked.Increment(ref requests) == 1
            ? Results.StatusCode(503) : Results.Text("deployed"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Urls.Single();
        var stages = new List<string>();
        var workflow = Workflow.Define("scenario-a", "1", w => w.Type("release-validation").Metadata("area", "checkout")
            .Variables(v => v.String("env", "ppe"))
            .SystemInit(ctx => { Assert.Equal("dev", ctx.Var<string>("env")); stages.Add("init"); })
            .PreSetup(_ => { stages.Add("pre"); })
            .Node("build", n => n.Task("command", Tasks.Command(
                OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows() ? ["/c", "exit 0"] : ["-c", "exit 0"])))
            .Node("deploy-eu", n => n.DependsOn("build").Task("http", Tasks.Http(HttpMethod.Get, address + "/deploy"),
                p => p.Retry(1, Backoff.Fixed(TimeSpan.Zero))))
            .Node("deploy-us", n => n.DependsOn("build").Task("function", ctx => { ctx.Outputs["region"] = "us"; }))
            .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us")
                .Input("deploy-eu", "response").Input("deploy-us", "region")
                .Task("join", ctx =>
                {
                    Assert.Equal("deployed", ctx.Input<HttpResponseOutput>("deploy-eu", "response")!.Body);
                    Assert.Equal("us", ctx.Input<string>("deploy-us", "region"));
                }))
            .PostProcess(_ => { stages.Add("post"); })
            .SystemPostProcess(_ => { stages.Add("sys-post"); }));
        using var output = new StringWriter();
        var result = await new LocalEngine(workflow, LocalVariables.Parse(workflow.Variables, ["--var", "env=dev"]), output)
            .RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, requests);
        Assert.Equal(["init", "pre", "post", "sys-post"], stages);
        Assert.Equal("release-validation", result.Snapshot.Metadata.Type);
        Assert.Equal("checkout", result.Snapshot.Metadata.Values["area"]);
        Assert.Equal("dev", result.Snapshot.Variables["env"].GetString());
        Assert.Equal(2, result.Attempts.Count(a => a.TaskKey == "deploy-eu/http"));
        Assert.Contains("Task smoke/join: Succeeded", output.ToString());
        var initial = new RunState(result.Snapshot.RunId, workflow.Nodes, workflow.Metadata, Assert.Fail,
            result.Snapshot.Variables).Snapshot;
        var replay = RunState.Restore(initial, result.Events, Assert.Fail);
        Assert.Equal(JsonSerializer.Serialize(result.Snapshot), JsonSerializer.Serialize(replay.Snapshot));
    }

    [Fact]
    public async Task RealNonRetryableCommandFailureReturnsExitOneAndRunsFailureCleanup()
    {
        var cleanup = false;
        var workflow = Workflow.Define("command-failure", "1", w => w.Type("test").Node("command", n => n
            .Task("exit", Tasks.Command(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows() ? ["/c", "exit 3"] : ["-c", "exit 3"], retryableExitCodes: []),
                p => p.Retry(3, Backoff.Fixed(TimeSpan.Zero))))
            .PostProcess(_ => { cleanup = true; }));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        var attempt = Assert.Single(result.Attempts, a => a.TaskKey == "command/exit");
        Assert.Equal(3, Assert.IsType<CommandTaskException>(attempt.Error).ExitCode);
        Assert.True(cleanup);
    }
}
