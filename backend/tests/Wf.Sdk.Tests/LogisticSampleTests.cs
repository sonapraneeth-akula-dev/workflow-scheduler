using System.Text.Json;
using HousingSample;
using RegressionSample;

namespace Wf.Sdk.Tests;

public sealed class LogisticSampleTests
{
    private static House[] Rows => Enumerable.Range(1, 200)
        .Select(index => new House($"1-{index}-1", 500 + index * 10, 200_000 + index * 8000)).ToArray();

    [Fact]
    public void Logistic_model_learns_binary_prices_without_validation_leakage()
    {
        var split = HousingData.Split(Rows);
        var fit = LogisticRegression.Train(split.Training, split.Validation, token: TestContext.Current.CancellationToken);
        var metrics = LogisticRegression.Evaluate(fit.Model, split.Test);
        Assert.Equal(201, fit.Learning.Length);
        Assert.True(fit.Learning[^1].TrainingLogLoss < fit.Learning[0].TrainingLogLoss / 2);
        Assert.InRange(metrics.Accuracy, 0.9, 1);
        Assert.True(metrics.Accuracy > metrics.BaselineAccuracy);
        Assert.Equal(metrics.Rows, metrics.TruePositives + metrics.FalsePositives + metrics.TrueNegatives + metrics.FalseNegatives);
        Assert.Equal(split.Training.Average(row => row.SquareFeet), fit.Model.AreaMean);
        var changed = split.Validation.Select(row => row with { Price = 3_000_000 - row.Price }).ToArray();
        Assert.Equal(fit.Model, LogisticRegression.Train(split.Training, changed,
            token: TestContext.Current.CancellationToken).Model);
        var outputs = new NodeOutputs("train");
        outputs.Set("model", fit.Model, LogisticJsonContext.Default.LogisticModel);
        Assert.Equal(fit.Model, outputs.Get("model", LogisticJsonContext.Default.LogisticModel));
        Assert.Equal(fit.Model, JsonSerializer.Deserialize(
            JsonSerializer.Serialize(fit.Model, LogisticJsonContext.Default.LogisticModel), LogisticJsonContext.Default.LogisticModel));
    }

    [Fact]
    public void Saturated_probabilities_and_undefined_precision_recall_remain_honest()
    {
        Assert.Equal(1, LogisticRegression.Sigmoid(1000));
        Assert.Equal(0, LogisticRegression.Sigmoid(-1000));
        var model = new LogisticModel(-1000, 0, 1000, 1, 1_000_000, 0.25);
        var metrics = LogisticRegression.Evaluate(model, Rows.Select(row => row with { Price = 200_000 }).ToArray());
        Assert.Null(metrics.Precision);
        Assert.Null(metrics.Recall);
        Assert.Equal(1, metrics.Accuracy);
        Assert.True(double.IsFinite(metrics.LogLoss));
        Assert.Throws<InvalidDataException>(() => model.PredictProbability(double.NaN));
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Evaluate(model with { AreaScale = 0 }, Rows));
    }

    [Fact]
    public void Invalid_training_data_and_options_fail_explicitly()
    {
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Train(Rows, Rows, priceThreshold: double.NaN, token: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Train(Rows, Rows, priceThreshold: 3_000_000, token: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Train(Rows, Rows, epochs: 0, token: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Train(Rows, Rows, learningRate: double.PositiveInfinity, token: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Train([], Rows, token: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Train(Rows.Select(row => row with { Price = 200_000 }).ToArray(), Rows, token: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LogisticRegression.Train(Rows.Select(row => row with { SquareFeet = 1000 }).ToArray(), Rows, token: TestContext.Current.CancellationToken));
        Assert.Throws<OperationCanceledException>(() => LogisticRegression.Train(Rows, Rows, token: new(true)));
    }

    [Fact]
    public void Seeded_scenarios_are_repeatable_and_zero_probability_really_disables_faults()
    {
        var first = RegressionScenario.Choose("random", 42);
        Assert.Equal(first, RegressionScenario.Choose("random", 42));
        Assert.Equal(first, RegressionScenario.Choose("random", first.Seed));
        foreach (var seed in Enumerable.Range(1, 100))
        {
            Assert.Equal("success", RegressionScenario.Choose("random", seed, timeoutPercent: 0,
                infraFailurePercent: 0, serviceFailurePercent: 0).Mode);
            Assert.NotEqual("test", RegressionScenario.Choose("task-timeout", seed, runFinalTest: false).FaultStage);
        }
        var generated = RegressionScenario.Choose("random", 0);
        Assert.True(generated.Seed > 0);
        Assert.Equal(generated, RegressionScenario.Choose("random", generated.Seed));
        var outputs = new NodeOutputs("sample");
        outputs.Set("scenario", first, LogisticJsonContext.Default.RegressionScenario);
        Assert.Equal(first, outputs.Get("scenario", LogisticJsonContext.Default.RegressionScenario));
    }

    [Fact]
    public void Random_distribution_and_all_forced_outcomes_are_testable()
    {
        var counts = Enumerable.Range(1, 10_000).Select(seed => RegressionScenario.Choose("random", seed))
            .GroupBy(scenario => scenario.Mode).ToDictionary(group => group.Key, group => group.Count());
        Assert.InRange(counts["success"], 6800, 7200);
        Assert.InRange(counts["task-timeout"], 400, 600);
        Assert.InRange(counts["run-timeout"], 400, 600);
        Assert.InRange(counts["infra-failure"], 900, 1100);
        Assert.InRange(counts["service-failure"], 900, 1100);
        foreach (var mode in new[] { "success", "task-timeout", "run-timeout", "infra-failure", "service-failure" })
            Assert.Equal(mode, RegressionScenario.Choose(mode, 1, "validate").Mode);
    }

    [Theory]
    [InlineData("unknown", 1, "random", 10, 10, 10, true)]
    [InlineData("random", -1, "random", 10, 10, 10, true)]
    [InlineData("random", 1, "unknown", 10, 10, 10, true)]
    [InlineData("random", 1, "random", -1, 10, 10, true)]
    [InlineData("random", 1, "random", 101, 0, 0, true)]
    [InlineData("random", 1, "random", 50, 50, 1, true)]
    [InlineData("task-timeout", 1, "test", 10, 10, 10, false)]
    public void Invalid_fault_configuration_is_rejected(string mode, int seed, string stage,
        int timeout, int infra, int service, bool runFinalTest) =>
        Assert.Throws<InvalidDataException>(() => RegressionScenario.Choose(mode, seed, stage, timeout, infra, service, runFinalTest));
}
