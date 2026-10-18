#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("platform/release/gateway-ready", "1.0.0", w => w
    .Type("release").Metadata("environment", "dev").Metadata("component", "gateway")
    .Node("readiness", n => n.Series()
        .Task("routes", ctx =>
        {
            string[] routes = ["/health", "/orders", "/catalog"];
            if (routes.Distinct(StringComparer.Ordinal).Count() != routes.Length)
                throw new InvalidOperationException("Sample routes must be unique.");
            ctx.Outputs["routesChecked"] = routes.Length;
            ctx.Log("Validated three in-memory routes; no gateway was contacted.");
        })
        .Task("summary", ctx => ctx.Log($"Ready: {ctx.Outputs.Get<int>("routesChecked")} routes."))));

return await workflow.RunAsync(args);
