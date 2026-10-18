#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;

var workflow = Workflow.Define("commerce/validation/payment-guard", "1.0.0", w => w
    .Type("validation").Metadata("environment", "dev").Metadata("component", "payments")
    .Variables(v => v.Bool("simulateFailure", true))
    .Node("validate", n => n.Task("synthetic-guard", ctx =>
    {
        ctx.Log("Synthetic payment guard; no payment processor was contacted.");
        if (ctx.Var<bool>("simulateFailure"))
            throw new TaskFailureException("Intentional demo validation failure; rerun with simulateFailure=false for success.",
                retryable: false, category: FailureCategory.ValidationError);
        ctx.Outputs["guardPassed"] = true;
    })));

return await workflow.RunAsync(args);
