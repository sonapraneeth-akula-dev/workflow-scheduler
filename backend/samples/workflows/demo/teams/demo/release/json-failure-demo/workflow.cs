#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:property PublishAot=false
using System.Security.Cryptography;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;

var agentExecution = args.Contains("--task-host", StringComparer.Ordinal);
var workflow = Workflow.Define("demo/release/json-failure-demo", "1.0.0", w => w
    .Type("release")
    .Metadata("environment", "dev")
    .Metadata("component", "dataflow-demo")
    .RunTimeout(TimeSpan.FromMinutes(5))
    .Defaults(p => p.Timeout(TimeSpan.FromSeconds(30)).NoAutoRetry())
    .Variables(v => v.String("mode", "random", allowed: ["random", "success", "retry", "failure"])
        .Int("seed", 0))
    .Node("sample", n => n.Task("choose", ctx =>
    {
        var mode = ctx.Var<string>("mode");
        var requestedSeed = ctx.Var<int>("seed");
        var seed = requestedSeed == 0 ? RandomNumberGenerator.GetInt32(1, int.MaxValue) : requestedSeed;
        var random = new Random(seed);
        var transientFailures = mode switch
        {
            "success" => 0,
            "retry" => 2,
            "failure" => 1,
            _ => random.Next(3)
        };
        var requiredFailure = mode == "failure" || mode == "random" && random.Next(4) == 0;
        var optionalFailure = mode is "retry" or "failure" || mode == "random" && random.Next(2) == 0;
        Record(ctx, new { mode, requestedSeed }, new
        {
            mode, seed, transientFailures, requiredFailure, optionalFailure,
            items = new[] { new { sku = "DEMO-001", quantity = 3, unitPriceCents = 1250 } }
        });
    }))
    .Node("fetch", n => n.DependsOn("sample").Input("sample", "choose")
        .Task("request", async ctx =>
        {
            var sample = ctx.Input<JsonElement>("sample", "choose").GetProperty("outputs");
            var transientFailures = sample.GetProperty("transientFailures").GetInt32();
            var failed = ctx.Attempt.Number <= transientFailures;
            await Task.Delay(TimeSpan.FromSeconds(1), ctx.CancellationToken);
            Record(ctx, sample, new
            {
                status = failed ? "Failed" : "Succeeded",
                attempt = ctx.Attempt.Number,
                simulated = true,
                items = sample.GetProperty("items")
            });
            if (failed)
                throw new TaskFailureException("Synthetic transient fetch failure; a bounded retry will follow.",
                    retryable: true, category: FailureCategory.TaskError);
        }, p => p.Timeout(TimeSpan.FromSeconds(30)).NoAutoRetry()
            .Retry(2, new(BackoffKind.Fixed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)),
                [FailureCategory.TaskError])))
    .Node("optional-check", n => n.DependsOn("sample").Input("sample", "choose")
        .Task("inspect", async ctx =>
        {
            var sample = ctx.Input<JsonElement>("sample", "choose").GetProperty("outputs");
            var failed = sample.GetProperty("optionalFailure").GetBoolean();
            await Task.Delay(TimeSpan.FromSeconds(1), ctx.CancellationToken);
            Record(ctx, new { optionalFailure = failed },
                new { status = failed ? "SoftFailed" : "Succeeded", simulated = true, note = "Optional diagnostic only." });
            if (failed)
                throw new TaskFailureException("Synthetic optional diagnostic failure; required work continues.",
                    retryable: false, category: FailureCategory.TaskError);
        }), failure: Failure.Soft)
    .Node("validate", n => n.DependsOn("sample", "fetch")
        .Input("sample", "choose").Input("fetch", "request")
        .Task("total", async ctx =>
        {
            var sample = ctx.Input<JsonElement>("sample", "choose").GetProperty("outputs");
            var fetched = ctx.Input<JsonElement>("fetch", "request").GetProperty("outputs");
            var failed = sample.GetProperty("requiredFailure").GetBoolean();
            var totalCents = fetched.GetProperty("items").EnumerateArray()
                .Sum(item => checked(item.GetProperty("quantity").GetInt32() * item.GetProperty("unitPriceCents").GetInt32()));
            await Task.Delay(TimeSpan.FromSeconds(1), ctx.CancellationToken);
            Record(ctx, new { requiredFailure = failed, items = fetched.GetProperty("items") },
                new { status = failed ? "Failed" : "Succeeded", totalCents, currency = "USD", simulated = true });
            if (failed)
                throw new TaskFailureException("Synthetic required validation failure; the JSON report still runs.",
                    retryable: false, category: FailureCategory.ValidationError);
        }))
    .Node("report", n => n.DependsOn("sample", DependencyCondition.Always)
        .DependsOn("fetch", DependencyCondition.Always)
        .DependsOn("optional-check", DependencyCondition.Always)
        .DependsOn("validate", DependencyCondition.Always)
        .Input("sample", "choose").Input("fetch", "request")
        .Task("publish-json", async ctx =>
        {
            var traces = new Dictionary<string, JsonElement>
            {
                ["sample/choose"] = ctx.Input<JsonElement>("sample", "choose"),
                ["fetch/request"] = ctx.Input<JsonElement>("fetch", "request")
            };
            var sample = traces["sample/choose"].GetProperty("outputs");
            var report = new
            {
                schemaVersion = 1,
                title = "Synthetic JSON dataflow and failure report",
                runId = ctx.RunId,
                generatedAt = DateTimeOffset.UtcNow,
                scenario = sample,
                expectedRequiredOutcome = sample.GetProperty("requiredFailure").GetBoolean() ? "Failed" : "Succeeded",
                fetchAttempts = traces["fetch/request"].GetProperty("outputs").GetProperty("attempt"),
                nodes = traces,
                note = "Sampled scenario and successful upstream data. Failed outputs are retained per attempt, not propagated as node inputs. Inspect Graph or Timeline for actual check statuses and independent retry attempts. Synthetic data only."
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(report);
            if (agentExecution)
                await ctx.Reports.PublishAsync("json", report.title, payload, ctx.CancellationToken);
            else
                ctx.Log("Local JSON report: " + System.Text.Encoding.UTF8.GetString(payload));
            Record(ctx, traces, new { renderer = "json", bytes = payload.Length, published = agentExecution });
        })));

return await workflow.RunAsync(args);

static void Record(TaskContext ctx, object inputs, object outputs)
{
    var key = ctx.TaskKey ?? throw new InvalidOperationException("A task identity is required.");
    var task = key[(key.IndexOf('/') + 1)..];
    var trace = JsonSerializer.SerializeToElement(new { inputs, outputs });
    ctx.Outputs[task] = trace;
    ctx.Log($"{ctx.TaskKey}: {trace.GetRawText()}");
}
