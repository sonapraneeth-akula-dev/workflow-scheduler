#:project ../../src/Wf.Sdk/Wf.Sdk.csproj
using Wf.Sdk;

// Authoring-only DAG sample; execution, built-in tasks and policies follow in their backlog items.
var workflow = Workflow.Define("payments/release-validation/deploy-check", "1.4.0", w => w
    .Type("release-validation")
    .Metadata("area", "checkout")
    .Node("build", n => n.Task("restore", _ => { }).Task("compile", _ => { }))
    .Node("deploy-eu", n => n.DependsOn("build").Task("deploy", _ => { }))
    .Node("deploy-us", n => n.DependsOn("build").Task("deploy", _ => { }))
    .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us").Parallel(maxParallel: 4)
        .Task("api", _ => { }).Task("ui", _ => { }).Task("perf-probe", _ => { })));

Console.WriteLine($"{workflow.Name}@{workflow.Version}: {workflow.Nodes.Length} nodes");
