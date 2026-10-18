#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("platform/maintenance/inventory-check", "1.0.0", w => w
    .Type("maintenance").Metadata("environment", "dev").Metadata("component", "inventory")
    .Node("audit", n => n.Task("unique-hosts", ctx =>
    {
        string[] hosts = ["sample-east", "sample-west", "sample-central"];
        if (hosts.Distinct(StringComparer.Ordinal).Count() != hosts.Length)
            throw new InvalidOperationException("Synthetic inventory contains duplicate hosts.");
        ctx.Outputs["hostCount"] = hosts.Length;
        ctx.Log($"Audited {hosts.Length} synthetic inventory entries; no real hosts were inspected.");
    })));

return await workflow.RunAsync(args);
