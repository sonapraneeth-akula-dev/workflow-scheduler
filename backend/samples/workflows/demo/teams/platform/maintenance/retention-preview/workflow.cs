#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("platform/maintenance/retention-preview", "1.0.0", w => w
    .Type("maintenance").Metadata("environment", "dev").Metadata("component", "retention")
    .Node("preview", n => n.Task("count-expired", ctx =>
    {
        int[] agesInDays = [2, 14, 35, 60];
        var expired = agesInDays.Count(age => age > 30);
        ctx.Outputs["wouldExpire"] = expired;
        ctx.Log($"Preview only: {expired} synthetic records exceed 30 days. Nothing was deleted.");
    })));

return await workflow.RunAsync(args);
