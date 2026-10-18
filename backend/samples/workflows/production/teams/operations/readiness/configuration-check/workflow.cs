#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("operations/readiness/configuration-check", "1.0.0", w => w
    .Type("readiness").Metadata("environment", "ppe").Metadata("component", "configuration")
    .Variables(v => v.Int("timeoutSeconds", 30))
    .Node("validate", n => n.Task("timeout", ctx =>
    {
        var timeout = ctx.Var<int>("timeoutSeconds");
        if (timeout is < 1 or > 300) throw new InvalidOperationException("timeoutSeconds must be between 1 and 300.");
        ctx.Outputs["timeoutSeconds"] = timeout;
        ctx.Log("Template configuration checked locally; no production service was contacted.");
    })));

return await workflow.RunAsync(args);
