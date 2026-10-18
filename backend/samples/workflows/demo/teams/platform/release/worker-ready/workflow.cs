#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("platform/release/worker-ready", "1.0.0", w => w
    .Type("release").Metadata("environment", "dev").Metadata("component", "worker")
    .Node("readiness", n => n.Task("queue-budget", ctx =>
    {
        const int concurrency = 4, batchSize = 16, maximumInFlight = 128;
        var inFlight = checked(concurrency * batchSize);
        if (inFlight > maximumInFlight) throw new InvalidOperationException("Sample queue budget exceeded.");
        ctx.Outputs["inFlight"] = inFlight;
        ctx.Log($"Validated local queue budget: {inFlight}/{maximumInFlight}; no queue was contacted.");
    })));

return await workflow.RunAsync(args);
