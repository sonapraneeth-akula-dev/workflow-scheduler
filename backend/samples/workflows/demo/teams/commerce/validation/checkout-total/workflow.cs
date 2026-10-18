#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("commerce/validation/checkout-total", "1.0.0", w => w
    .Type("validation").Metadata("environment", "dev").Metadata("component", "checkout")
    .Node("validate", n => n.Task("total", ctx =>
    {
        int[] lineTotalsCents = [1200, 3500, 800];
        var total = lineTotalsCents.Sum();
        if (total != 5500) throw new InvalidOperationException("Synthetic checkout total is incorrect.");
        ctx.Outputs["totalCents"] = total;
        ctx.Log($"Synthetic checkout total verified: {total} cents. No order was submitted.");
    })));

return await workflow.RunAsync(args);
