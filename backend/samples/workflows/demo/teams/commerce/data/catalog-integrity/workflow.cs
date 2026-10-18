#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("commerce/data/catalog-integrity", "1.0.0", w => w
    .Type("data").Metadata("environment", "dev").Metadata("component", "catalog")
    .Node("inspect", n => n.Task("products", ctx =>
    {
        (string Sku, int PriceCents)[] products = [("sample-a", 1200), ("sample-b", 3500)];
        if (products.Any(product => product.PriceCents <= 0) ||
            products.Select(product => product.Sku).Distinct(StringComparer.Ordinal).Count() != products.Length)
            throw new InvalidOperationException("Synthetic catalog contains invalid products.");
        ctx.Outputs["productsChecked"] = products.Length;
        ctx.Log($"Checked {products.Length} in-memory products; no catalog was queried or changed.");
    })));

return await workflow.RunAsync(args);
