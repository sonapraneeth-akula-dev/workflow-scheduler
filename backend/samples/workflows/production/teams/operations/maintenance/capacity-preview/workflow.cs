#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("operations/maintenance/capacity-preview", "1.0.0", w => w
    .Type("maintenance").Metadata("environment", "ppe").Metadata("component", "capacity")
    .Variables(v => v.Int("replicas", 3).Int("unitsPerReplica", 100))
    .Node("preview", n => n.Task("capacity", ctx =>
    {
        var replicas = ctx.Var<int>("replicas");
        var units = ctx.Var<int>("unitsPerReplica");
        if (replicas is < 1 or > 100 || units is < 1 or > 10000)
            throw new InvalidOperationException("Use 1-100 replicas and 1-10000 units per replica.");
        ctx.Outputs["capacityUnits"] = checked(replicas * units);
        ctx.Log("Template preview only; no infrastructure was provisioned or changed.");
    })));

return await workflow.RunAsync(args);
