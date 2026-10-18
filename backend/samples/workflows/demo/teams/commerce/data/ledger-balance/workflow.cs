#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Sdk;

var workflow = Workflow.Define("commerce/data/ledger-balance", "1.0.0", w => w
    .Type("data").Metadata("environment", "dev").Metadata("component", "ledger")
    .Node("reconcile", n => n.Task("balance", ctx =>
    {
        int[] entriesCents = [1500, -400, -1100];
        var balance = entriesCents.Sum();
        if (balance != 0) throw new InvalidOperationException("Synthetic ledger is not balanced.");
        ctx.Outputs["balanceCents"] = balance;
        ctx.Log("In-memory sample ledger balances to zero; no financial system was accessed.");
    })));

return await workflow.RunAsync(args);
