#:project ../../src/Wf.Sdk/Wf.Sdk.csproj
using Wf.Sdk;
using Wf.Sdk.Local;

var workflow = Workflow.Define("payments/release-validation/deploy-check", "1.4.0", w => w
    .Type("release-validation")
    .Metadata("area", "checkout")
    .Variables(v => v.String("env", "dev", allowed: ["dev", "ppe"]))
    .SystemInit(ctx => ctx.Log($"Preparing sample in {ctx.Var<string>("env")}."))
    .Node("build", n => n.Task("sdk", Tasks.Command("dotnet", "--version"))
        .Task("compile", ctx => ctx.Log("Sample build complete.")))
    .Node("deploy-eu", n => n.DependsOn("build").Task("deploy", ctx => ctx.Log("Sample EU deployment.")))
    .Node("deploy-us", n => n.DependsOn("build").Task("deploy", ctx => ctx.Log("Sample US deployment.")))
    .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us").Parallel(maxParallel: 4)
        .Task("api", ctx => ctx.Log("Sample API check."))
        .Task("ui", ctx => ctx.Log("Sample UI check."))
        .Task("perf-probe", ctx => ctx.Log("Sample performance probe."), failure: Failure.Soft))
    .PostProcess(ctx => ctx.Log(ctx.Abort is { } abort ? $"Stopped: {abort.Reason}" : "Sample cleanup complete.")));

var engine = new LocalEngine(workflow, LocalVariables.Parse(workflow.Variables, args));
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try { return (await engine.RunAsync(cancellation.Token)).ExitCode; }
finally { Console.CancelKeyPress -= cancel; }
