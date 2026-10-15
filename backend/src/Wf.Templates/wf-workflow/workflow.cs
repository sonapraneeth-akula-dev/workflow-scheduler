#:package Wf.Sdk
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("WorkflowSample", "1.0.0", w => w
    .Type("validation")
    .Variables(v => v.String("env", "dev", allowed: ["dev", "ppe"]))
    .Node("prepare", n => n.Task("work", ctx => ctx.Log($"Preparing {ctx.Var<string>("env")}."),
        p => p.Retry(2)))
    .Node("check", n => n.DependsOn("prepare")
        .Task("work", ctx => ctx.Log("Workflow checks passed."))));

return await workflow.RunAsync(args);
