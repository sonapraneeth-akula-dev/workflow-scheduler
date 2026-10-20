#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:project ./model/LogisticModel.csproj
#:property PublishAot=false
using System.Text;
using System.Text.Json;
using HousingSample;
using RegressionSample;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;

var hosted = args.Contains("--task-host", StringComparer.Ordinal);
var workflow = Workflow.Define("data-science/regression/logistic-regression", "1.0.0", w => w
    .Type("regression")
    .Metadata("dataset", "nyc-housing")
    .Affinity(new PlacementAffinity(AffinityKind.Run))
    .RunTimeout(TimeSpan.FromSeconds(RegressionScenario.RunTimeoutSeconds))
    .Defaults(p => p.Timeout(TimeSpan.FromSeconds(45)).NoAutoRetry())
    .Variables(v => v.String("mode", "random", allowed:
            ["random", "success", "task-timeout", "run-timeout", "infra-failure", "service-failure"])
        .Int("seed", 0).String("faultStage", "random", allowed: ["random", "train", "validate", "test"])
        .Int("timeoutPercent", 10).Int("infraFailurePercent", 10).Int("serviceFailurePercent", 10)
        .Double("priceThreshold", 1_000_000).Int("epochs", 200).Double("learningRate", 0.1)
        .Bool("runFinalTest", true))
    .Node("sample", n => n.Task("choose-outcome", async ctx =>
    {
        RegressionScenario scenario;
        try
        {
            LogisticRegression.ValidateOptions(ctx.Var<double>("priceThreshold"), ctx.Var<int>("epochs"),
                ctx.Var<double>("learningRate"));
            scenario = RegressionScenario.Choose(ctx.Var<string>("mode"), ctx.Var<int>("seed"),
                ctx.Var<string>("faultStage"), ctx.Var<int>("timeoutPercent"), ctx.Var<int>("infraFailurePercent"),
                ctx.Var<int>("serviceFailurePercent"), ctx.Var<bool>("runFinalTest"));
        }
        catch (InvalidDataException error)
        {
            throw new TaskFailureException("Regression sample service validation failure: " + error.Message,
                retryable: false, category: FailureCategory.ValidationError);
        }
        ctx.Outputs.Set("scenario", scenario, LogisticJsonContext.Default.RegressionScenario);
        ctx.Outputs["runTimeout"] = scenario.Mode == "run-timeout";
        ctx.Log($"Recorded scenario: mode={scenario.Mode}; seed={scenario.Seed}; faultStage={scenario.FaultStage}. No resampling during retries.");
        await Report(ctx, hosted, "json", "Logistic regression configuration and sampled outcome", new
        {
            scenario,
            configuration = new
            {
                priceThreshold = ctx.Var<double>("priceThreshold"), epochs = ctx.Var<int>("epochs"),
                learningRate = ctx.Var<double>("learningRate"), runFinalTest = ctx.Var<bool>("runFinalTest"),
                taskTimeoutSeconds = RegressionScenario.TaskTimeoutSeconds,
                runTimeoutSeconds = RegressionScenario.RunTimeoutSeconds
            },
            source = HousingData.Source,
            target = "Sale price at or above the configured USD threshold; probability cutoff 0.5.",
            split = "The existing verified NYC housing cache: 70% training, 15% validation, 15% held-out test.",
            note = "Real public sales and computed model metrics. Only the selected latency/infrastructure/service fault is synthetic. Task timeouts fail a task; a run deadline produces a TimedOut run."
        });
    }))
    .Node("run-budget", n => n.DependsOn("sample").Input("sample", "scenario").Input("sample", "runTimeout")
        .When(Condition.Output("sample", "runTimeout"))
        .Task("wait-for-run-deadline", async ctx =>
        {
            ctx.Log($"Intentional run timeout: waiting for the actual {RegressionScenario.RunTimeoutSeconds}-second run deadline. Seed={Scenario(ctx).Seed}.");
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
        }, p => p.Timeout(TimeSpan.FromSeconds(RegressionScenario.RunTimeoutSeconds))))
    .Node("dataset", n => n.DependsOn("sample").DependsOn("run-budget", DependencyCondition.Always)
        .Task("load-verified-sales", async ctx =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            Dataset data;
            try
            {
                data = await HousingData.EnsureAsync(HousingData.CacheDirectory(), http, ctx.Log, ctx.CancellationToken);
            }
            catch (HttpRequestException error)
            {
                throw new TaskFailureException("Regression sample infrastructure failure: NYC sales download failed: " + error.Message,
                    retryable: false, category: FailureCategory.PlatformError);
            }
            ctx.Outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
            await Report(ctx, hosted, "json", "Verified classification dataset", data);
        }, p => p.Timeout(TimeSpan.FromSeconds(90))))
    .Node("train", n => n.DependsOn("sample", "dataset").Input("sample", "scenario").Input("dataset", "dataset")
        .Task("fit-logistic-regression", async ctx =>
        {
            await Inject(ctx, "train");
            var data = Data(ctx, "dataset");
            var training = HousingData.Read(data.Directory, "training");
            LogisticFit fit;
            try
            {
                fit = LogisticRegression.Train(training, HousingData.Read(data.Directory, "validation"),
                    ctx.Var<double>("priceThreshold"), ctx.Var<int>("epochs"), ctx.Var<double>("learningRate"), ctx.CancellationToken);
            }
            catch (InvalidDataException error)
            {
                throw new TaskFailureException("Regression sample service validation failure: " + error.Message,
                    retryable: false, category: FailureCategory.ValidationError);
            }
            ctx.Outputs.Set("model", fit.Model, LogisticJsonContext.Default.LogisticModel);
            ctx.Outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
            await Report(ctx, hosted, "json", "Fitted logistic regression model", fit.Model);
            await Report(ctx, hosted, "chart", "Training and validation log loss", new
            {
                type = "line", xLabel = "Epoch", yLabel = "Log loss",
                x = fit.Learning.Select(point => point.Epoch),
                series = new[]
                {
                    new { label = "Training log loss", values = fit.Learning.Select(point => point.TrainingLogLoss) },
                    new { label = "Validation log loss", values = fit.Learning.Select(point => point.ValidationLogLoss) }
                }
            });
        }, p => p.Timeout(TimeSpan.FromSeconds(RegressionScenario.TaskTimeoutSeconds))))
    .Node("validate", n => n.DependsOn("sample", "train").Input("sample", "scenario")
        .Input("train", "model").Input("train", "dataset")
        .Task("measure-validation-quality", async ctx =>
        {
            await Inject(ctx, "validate");
            var data = Data(ctx, "train");
            var model = Model(ctx, "train");
            var metrics = LogisticRegression.Evaluate(model, HousingData.Read(data.Directory, "validation"));
            ctx.Outputs.Set("model", model, LogisticJsonContext.Default.LogisticModel);
            ctx.Outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
            ctx.Outputs.Set("metrics", metrics, LogisticJsonContext.Default.LogisticMetrics);
            await Report(ctx, hosted, "json", "Validation classification metrics (not final test)", metrics);
        }, p => p.Timeout(TimeSpan.FromSeconds(RegressionScenario.TaskTimeoutSeconds))))
    .Node("test", n => n.DependsOn("sample", "validate").Input("sample", "scenario")
        .Input("validate", "model").Input("validate", "dataset").When(Condition.Variable("runFinalTest"))
        .Task("evaluate-held-out-classification", async ctx =>
        {
            await Inject(ctx, "test");
            var data = Data(ctx, "validate");
            var model = Model(ctx, "validate");
            var test = HousingData.Read(data.Directory, "test");
            var metrics = LogisticRegression.Evaluate(model, test);
            ctx.Outputs.Set("metrics", metrics, LogisticJsonContext.Default.LogisticMetrics);
            ctx.Log($"Held-out classification: accuracy={metrics.Accuracy:P2}; log loss={metrics.LogLoss:F4}; rows={metrics.Rows}. Baseline accuracy={metrics.BaselineAccuracy:P2}.");
            await Report(ctx, hosted, "json", "Final held-out classification metrics and confusion counts", metrics);
            var csv = "property_id,square_feet,actual_price_usd,actual_positive,predicted_probability,predicted_positive\n" +
                string.Join("\n", test.Select(row => FormattableString.Invariant(
                    $"{row.PropertyId},{row.SquareFeet:R},{row.Price:R},{row.Price >= model.PriceThreshold},{model.PredictProbability(row.SquareFeet):R},{model.PredictProbability(row.SquareFeet) >= 0.5}")));
            await Publish(ctx, hosted, "csv", "Every held-out classification prediction", Encoding.UTF8.GetBytes(csv));
        }, p => p.Timeout(TimeSpan.FromSeconds(RegressionScenario.TaskTimeoutSeconds)))));

return await workflow.RunAsync(args);

static RegressionScenario Scenario(TaskContext ctx) =>
    ctx.Input("sample", "scenario", LogisticJsonContext.Default.RegressionScenario)
        ?? throw new InvalidDataException("The committed sampled regression scenario is missing.");

static Dataset Data(TaskContext ctx, string node) =>
    ctx.Input(node, "dataset", HousingJsonContext.Default.Dataset)
        ?? throw new InvalidDataException("The committed verified housing dataset is missing.");

static LogisticModel Model(TaskContext ctx, string node) =>
    ctx.Input(node, "model", LogisticJsonContext.Default.LogisticModel)
        ?? throw new InvalidDataException("The committed logistic model is missing.");

static async Task Inject(TaskContext ctx, string stage)
{
    var scenario = Scenario(ctx);
    if (scenario.FaultStage != stage) return;
    switch (scenario.Mode)
    {
        case "task-timeout":
            ctx.Log($"Intentional task timeout in {ctx.TaskKey}: waiting for its actual {RegressionScenario.TaskTimeoutSeconds}-second deadline. Seed={scenario.Seed}.");
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
            break;
        case "infra-failure":
            throw new TaskFailureException(
                $"Regression sample infrastructure failure: feature store connection refused during {stage}; seed={scenario.Seed}. Deliberate demo fault; no real infrastructure was disrupted.",
                retryable: false, category: FailureCategory.PlatformError);
        case "service-failure":
            throw new TaskFailureException(
                $"Regression sample service validation failure: classification quality gate deliberately rejected {stage}; seed={scenario.Seed}. The sampled demo fault is not a measured model-quality result.",
                retryable: false, category: FailureCategory.ValidationError);
    }
}

static Task Report(TaskContext ctx, bool hosted, string kind, string title, object value) =>
    Publish(ctx, hosted, kind, title, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static Task Publish(TaskContext ctx, bool hosted, string kind, string title, byte[] bytes)
{
    if (hosted) return ctx.Reports.PublishAsync(kind, title, bytes, ctx.CancellationToken);
    ctx.Log($"Report [{kind}] {title}: {Encoding.UTF8.GetString(bytes)}");
    return Task.CompletedTask;
}
