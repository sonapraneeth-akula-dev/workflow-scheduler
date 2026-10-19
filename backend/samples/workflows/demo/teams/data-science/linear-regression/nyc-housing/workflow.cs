#:project ../../../../../../../src/Wf.Sdk/Wf.Sdk.csproj
#:project ./model/HousingModel.csproj
#:property PublishAot=false
using System.Text;
using System.Text.Json;
using HousingSample;
using Wf.Contracts.Definition;
using Wf.Sdk;

var hosted = args.Contains("--task-host", StringComparer.Ordinal);
var workflow = Workflow.Define("data-science/linear-regression/nyc-housing", "1.1.0", w => w
    .Type("linear-regression")
    .Metadata("dataset", "nyc-housing")
    .Variables(v => v.Bool("runFinalTest", true))
    .Affinity(new PlacementAffinity(AffinityKind.Run))
    .Node("dataset", n => n.Series()
        .Task("check-cache", async ctx =>
        {
            var cached = await HousingData.TryLoadAsync(HousingData.CacheDirectory(), ctx.Log, ctx.CancellationToken);
            ctx.Outputs["cacheMissing"] = cached is null;
            if (cached is not null) ctx.Outputs.Set("dataset", cached, HousingJsonContext.Default.Dataset);
        })
        .Task("download-if-missing", async ctx =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var data = await HousingData.EnsureAsync(HousingData.CacheDirectory(), http, ctx.Log, ctx.CancellationToken);
            ctx.Outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
        }, p => p.Timeout(TimeSpan.FromMinutes(5)), when: Condition.Output("dataset", "cacheMissing"))
        .Task("load-and-report", async ctx =>
        {
            var verified = await HousingData.TryLoadAsync(HousingData.CacheDirectory(), ctx.Log, ctx.CancellationToken)
                ?? throw new InvalidDataException("Dataset cache disappeared before loading.");
            var data = verified with { Reused = ctx.Outputs.Get("dataset", HousingJsonContext.Default.Dataset)!.Reused };
            ctx.Outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
            await Report(ctx, hosted, "json", "Dataset and cache", new
            {
                data,
                feature = "Gross building area (square feet)",
                target = "Sale price (USD)",
                filters = "Tax class 1, 500-5000 square feet, $100,000-$3,000,000; latest sale per property.",
                split = "Fixed SHA-256 ordering of property IDs: 70% training, 15% validation, 15% final test. No property overlaps.",
                cache = data.Reused ? "Verified cache; download not executed." : "Downloaded, verified and stored on disk."
            });
        }))
    .Node("train", n => n.DependsOn("dataset").Input("dataset", "dataset").Series()
        .Task("fit-linear-regression", async ctx =>
        {
            var data = ctx.Input("dataset", "dataset", HousingJsonContext.Default.Dataset)!;
            var training = HousingData.Read(data.Directory, "training");
            var fit = HousingData.Train(training, HousingData.Read(data.Directory, "validation"), ctx.CancellationToken);
            ctx.Outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
            ctx.Outputs.Set("model", fit.Model, HousingJsonContext.Default.Model);
            ctx.Log($"Trained price = {fit.Model.Intercept:F2} + {fit.Model.Slope:F2} * standardized square feet.");
            await Report(ctx, hosted, "chart", "Training and validation RMSE by epoch (USD)", new
            {
                type = "line",
                x = fit.Learning.Select(point => point.Epoch),
                series = new[]
                {
                    new { label = "Training RMSE (USD)", values = fit.Learning.Select(point => point.TrainingRmse) },
                    new { label = "Validation RMSE (USD)", values = fit.Learning.Select(point => point.ValidationRmse) }
                }
            });
            var bins = training.GroupBy(row => (int)(row.SquareFeet / 250)).OrderBy(group => group.Key).ToArray();
            await Report(ctx, hosted, "chart", "Training prices and fitted line by floor area", new
            {
                type = "line",
                x = bins.Select(group => group.Average(row => row.SquareFeet)),
                series = new[]
                {
                    new { label = "Mean sale price (USD)", values = bins.Select(group => group.Average(row => row.Price)) },
                    new { label = "Fitted price (USD)", values = bins.Select(group => fit.Model.Predict(group.Average(row => row.SquareFeet))) }
                }
            });
            await Report(ctx, hosted, "json", "Fitted linear regression model", fit.Model);
        }))
    .Node("validate", n => n.DependsOn("train").Input("train", "model").Input("train", "dataset").Series()
        .Task("measure-validation-error", async ctx =>
        {
            var data = ctx.Input("train", "dataset", HousingJsonContext.Default.Dataset)!;
            var model = ctx.Input("train", "model", HousingJsonContext.Default.Model)!;
            var metrics = HousingData.Evaluate(model, HousingData.Read(data.Directory, "validation"));
            ctx.Outputs.Set("metrics", metrics, HousingJsonContext.Default.Metrics);
            ctx.Outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
            ctx.Outputs.Set("model", model, HousingJsonContext.Default.Model);
            await Report(ctx, hosted, "json", "Validation metrics (not final test)", metrics);
        }))
    .Node("test", n => n.DependsOn("validate").Input("validate", "model").Input("validate", "dataset")
        .When(Condition.Variable("runFinalTest")).Series()
        .Task("evaluate-held-out-test", async ctx =>
        {
            var data = ctx.Input("validate", "dataset", HousingJsonContext.Default.Dataset)!;
            var model = ctx.Input("validate", "model", HousingJsonContext.Default.Model)!;
            var test = HousingData.Read(data.Directory, "test");
            var metrics = HousingData.Evaluate(model, test);
            ctx.Outputs.Set("metrics", metrics, HousingJsonContext.Default.Metrics);
            ctx.Log($"Final test: R2={metrics.R2:F4}; MAE=${metrics.Mae:F2}; RMSE=${metrics.Rmse:F2}; rows={metrics.Rows}.");
            await Report(ctx, hosted, "json", "Final held-out test metrics", metrics);
            await Report(ctx, hosted, "table", "Final test results", new
            {
                columns = new[] { "Metric", "Value", "Meaning" },
                rows = new[]
                {
                    new[] { "Test properties", metrics.Rows.ToString(), "Never used to fit the model" },
                    new[] { "R squared", metrics.R2?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "Undefined", "1 is perfect; 0 predicts the test mean; negative is possible" },
                    new[] { "MAE (USD)", metrics.Mae.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), "Mean absolute prediction error; lower is better" },
                    new[] { "RMSE (USD)", metrics.Rmse.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), "Root mean square error; lower is better" },
                    new[] { "Training-mean baseline RMSE (USD)", metrics.BaselineRmse.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), "Comparison against always predicting the training mean" }
                }
            });
            var chartRows = test.OrderBy(row => row.Price).Take(100).ToArray();
            await Report(ctx, hosted, "chart", "Final test: actual versus predicted prices (first 100 by actual price)", new
            {
                type = "line",
                x = Enumerable.Range(1, chartRows.Length),
                series = new[]
                {
                    new { label = "Actual price (USD)", values = chartRows.Select(row => row.Price) },
                    new { label = "Predicted price (USD)", values = chartRows.Select(row => model.Predict(row.SquareFeet)) }
                }
            });
            var csv = "property_id,square_feet,actual_price_usd,predicted_price_usd,error_usd\n" +
                string.Join("\n", test.Select(row => FormattableString.Invariant(
                    $"{row.PropertyId},{row.SquareFeet:R},{row.Price:R},{model.Predict(row.SquareFeet):F2},{model.Predict(row.SquareFeet) - row.Price:F2}")));
            await Publish(ctx, hosted, "csv", "Every held-out test prediction (USD)", Encoding.UTF8.GetBytes(csv));
            await Publish(ctx, hosted, "markdown", "How to read this experiment", Encoding.UTF8.GetBytes(
                "# NYC housing: a deliberately simple baseline\n\n" +
                "Dataset -> Train -> Validate -> Test. A verified cache hit marks the download task Skipped; no HTTP request or attempt. " +
                "Set runFinalTest=false to mark the final test node and its task Skipped while retaining training and validation reports.\n\n" +
                "One input: building area. One target: sale price in USD. Gradient descent runs 100 epochs; area normalization uses training rows only. " +
                "Validation is monitored, not used to fit coefficients. The test split is read only after validation completes.\n\n" +
                "R squared, MAE and RMSE measure regression quality; there is no classification accuracy percentage. " +
                "Compare final RMSE with the training-mean baseline. This small model ignores location, condition and many other price drivers, so expect substantial error. " +
                "It is an educational workflow, not a property valuation tool.\n\n" +
                "Source: NYC Department of Finance rolling sales, NYC Open Data dataset `usep-8jbt`. " +
                "Cache includes the exact source snapshot, split CSVs and SHA-256 checksums. No fabricated data or metrics."));
        })));

return await workflow.RunAsync(args);

static Task Report(TaskContext context, bool hosted, string kind, string title, object value) =>
    Publish(context, hosted, kind, title, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static Task Publish(TaskContext context, bool hosted, string kind, string title, byte[] bytes)
{
    if (hosted) return context.Reports.PublishAsync(kind, title, bytes, context.CancellationToken);
    context.Log($"Report [{kind}] {title}: {Encoding.UTF8.GetString(bytes)}");
    return Task.CompletedTask;
}
