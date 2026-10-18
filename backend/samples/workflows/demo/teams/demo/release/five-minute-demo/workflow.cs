#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;

var agentExecution = args.Contains("--task-host", StringComparer.Ordinal);
var workflow = Workflow.Define("demo/release/five-minute-demo", "1.0.1", w => w
    .Type("release")
    .Metadata("environment", "dev")
    .Metadata("component", "dataflow-demo")
    .Variables(v => v.String("customer", "Sample customer")
        .Int("quantity", 3).Int("unitPriceCents", 1250).Int("discountPercent", 10))
    .Node("order", n => n.Series()
        .Task("load", ctx =>
        {
            var order = new
            {
                customer = ctx.Var<string>("customer"),
                quantity = ctx.Var<int>("quantity"),
                unitPriceCents = ctx.Var<int>("unitPriceCents"),
                discountPercent = ctx.Var<int>("discountPercent")
            };
            Record(ctx, "load", order, order);
        })
        .Task("validate", ctx =>
        {
            var order = ctx.Outputs.Get<JsonElement>("load").GetProperty("outputs");
            if (string.IsNullOrWhiteSpace(order.GetProperty("customer").GetString()) ||
                order.GetProperty("customer").GetString()!.Length > 80 ||
                order.GetProperty("quantity").GetInt32() is < 1 or > 100 ||
                order.GetProperty("unitPriceCents").GetInt32() is < 1 or > 100_000 ||
                order.GetProperty("discountPercent").GetInt32() is < 0 or > 100)
                throw new TaskFailureException(
                    "Use a nonblank customer (up to 80 characters), quantity 1-100, unitPriceCents 1-100000 and discountPercent 0-100.",
                    retryable: false, category: FailureCategory.ValidationError);
            Record(ctx, "validate", order, order);
        }))
    .Node("pricing", n => n.DependsOn("order").Input("order", "validate").Series()
        .Task("subtotal", ctx =>
        {
            var order = ctx.Input<JsonElement>("order", "validate").GetProperty("outputs");
            var quantity = order.GetProperty("quantity").GetInt32();
            var unitPriceCents = order.GetProperty("unitPriceCents").GetInt32();
            Record(ctx, "subtotal", new { quantity, unitPriceCents },
                new { subtotalCents = checked(quantity * unitPriceCents) });
        })
        .Task("discount", ctx =>
        {
            var subtotalCents = ctx.Outputs.Get<JsonElement>("subtotal")
                .GetProperty("outputs").GetProperty("subtotalCents").GetInt32();
            var discountPercent = ctx.Input<JsonElement>("order", "validate")
                .GetProperty("outputs").GetProperty("discountPercent").GetInt32();
            var discountCents = checked(subtotalCents * discountPercent) / 100;
            Record(ctx, "discount", new { subtotalCents, discountPercent },
                new { discountCents, merchandiseCents = subtotalCents - discountCents });
        }))
    .Node("shipping", n => n.DependsOn("order").Input("order", "validate").Series()
        .Task("select-service", ctx =>
        {
            var quantity = ctx.Input<JsonElement>("order", "validate")
                .GetProperty("outputs").GetProperty("quantity").GetInt32();
            Record(ctx, "select-service", new { quantity },
                new { service = quantity > 3 ? "bulk" : "standard" });
        })
        .Task("quote", ctx =>
        {
            var service = ctx.Outputs.Get<JsonElement>("select-service")
                .GetProperty("outputs").GetProperty("service").GetString();
            Record(ctx, "quote", new { service },
                new { shippingCents = service == "bulk" ? 1200 : 500 });
        }))
    .Node("invoice", n => n.DependsOn("order", "pricing", "shipping")
        .Input("order", "load").Input("order", "validate")
        .Input("pricing", "subtotal").Input("pricing", "discount")
        .Input("shipping", "select-service").Input("shipping", "quote").Series()
        .Task("total", ctx =>
        {
            var customer = ctx.Input<JsonElement>("order", "validate")
                .GetProperty("outputs").GetProperty("customer").GetString();
            var merchandiseCents = ctx.Input<JsonElement>("pricing", "discount")
                .GetProperty("outputs").GetProperty("merchandiseCents").GetInt32();
            var shippingCents = ctx.Input<JsonElement>("shipping", "quote")
                .GetProperty("outputs").GetProperty("shippingCents").GetInt32();
            Record(ctx, "total", new { customer, merchandiseCents, shippingCents },
                new { customer, totalCents = checked(merchandiseCents + shippingCents), currency = "USD" });
        })
        .Task("publish-report", async ctx =>
        {
            var total = ctx.Outputs.Get<JsonElement>("total").GetProperty("outputs");
            Record(ctx, "publish-report", total,
                new { totalCents = total.GetProperty("totalCents").GetInt32(), nodeCount = 4, taskCount = 8 });
            var taskKeys = new Dictionary<string, string[]>
            {
                ["order"] = ["load", "validate"],
                ["pricing"] = ["subtotal", "discount"],
                ["shipping"] = ["select-service", "quote"],
                ["invoice"] = ["total", "publish-report"]
            };
            var outputs = taskKeys.ToDictionary(node => node.Key, node => node.Value.ToDictionary(
                task => task, task => node.Key == "invoice"
                    ? ctx.Outputs.Get<JsonElement>(task)
                    : ctx.Input<JsonElement>(node.Key, task)));
            var orderInput = new { order = new { validate = outputs["order"]["validate"] } };
            var inputs = new Dictionary<string, object>
            {
                ["order"] = outputs["order"]["load"].GetProperty("inputs"),
                ["pricing"] = orderInput,
                ["shipping"] = orderInput,
                ["invoice"] = new
                {
                    order = outputs["order"], pricing = outputs["pricing"], shipping = outputs["shipping"]
                }
            };
            var rows = new List<string[]>();
            foreach (var (node, tasks) in outputs)
            {
                rows.Add(["Node", node, "-", JsonSerializer.Serialize(inputs[node]), JsonSerializer.Serialize(tasks)]);
                foreach (var (task, trace) in tasks)
                    rows.Add(["Task", node, task, trace.GetProperty("inputs").GetRawText(),
                        trace.GetProperty("outputs").GetRawText()]);
            }
            var report = JsonSerializer.SerializeToUtf8Bytes(new
            {
                columns = new[] { "Scope", "Node", "Task", "Inputs (JSON)", "Outputs (JSON)" }, rows
            });
            if (agentExecution)
                await ctx.Reports.PublishAsync("table", "Node and task inputs / outputs", report,
                    ctx.CancellationToken);
            else
                ctx.Log("Local dataflow report: " + System.Text.Encoding.UTF8.GetString(report));
        })));

return await workflow.RunAsync(args);

static void Record(TaskContext context, string task, object inputs, object outputs)
{
    var trace = JsonSerializer.SerializeToElement(new { inputs, outputs });
    context.Outputs[task] = trace;
    context.Log($"{context.TaskKey}: {trace.GetRawText()}");
}
