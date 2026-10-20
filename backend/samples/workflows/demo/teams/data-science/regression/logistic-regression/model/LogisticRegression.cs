using System.Text.Json.Serialization;
using HousingSample;

namespace RegressionSample;

public sealed record LogisticModel(double Intercept, double Slope, double AreaMean, double AreaScale,
    double PriceThreshold, double TrainingPositiveRate)
{
    public double PredictProbability(double squareFeet)
    {
        if (!double.IsFinite(squareFeet)) throw new InvalidDataException("Prediction area must be finite.");
        return LogisticRegression.Sigmoid(Intercept + Slope * ((squareFeet - AreaMean) / AreaScale));
    }
}

public sealed record LogisticLearningPoint(int Epoch, double TrainingLogLoss, double ValidationLogLoss);
public sealed record LogisticFit(LogisticModel Model, LogisticLearningPoint[] Learning);
public sealed record LogisticMetrics(int Rows, double Accuracy, double LogLoss, double? Precision, double? Recall,
    int TruePositives, int FalsePositives, int TrueNegatives, int FalseNegatives, double BaselineAccuracy);

[JsonSerializable(typeof(LogisticModel))]
[JsonSerializable(typeof(LogisticMetrics))]
[JsonSerializable(typeof(RegressionScenario))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class LogisticJsonContext : JsonSerializerContext;

public static class LogisticRegression
{
    public static double Sigmoid(double value) => value >= 0
        ? 1 / (1 + Math.Exp(-value))
        : Math.Exp(value) / (1 + Math.Exp(value));

    public static void ValidateOptions(double priceThreshold, int epochs, double learningRate)
    {
        HousingData.ValidateTrainingOptions(epochs, learningRate);
        if (!double.IsFinite(priceThreshold) || priceThreshold is <= 100_000 or >= 3_000_000)
            throw new InvalidDataException("The positive-class price threshold must be finite and between $100,000 and $3,000,000.");
    }

    public static LogisticFit Train(House[] training, House[] validation, double priceThreshold = 1_000_000,
        int epochs = 200, double learningRate = 0.1, CancellationToken token = default)
    {
        ValidateOptions(priceThreshold, epochs, learningRate);
        ValidateRows(training);
        ValidateRows(validation);
        var mean = training.Average(row => row.SquareFeet);
        var scale = Math.Sqrt(training.Average(row => Math.Pow(row.SquareFeet - mean, 2)));
        var prior = training.Count(row => row.Price >= priceThreshold) / (double)training.Length;
        if (!double.IsFinite(scale) || scale <= 0 || prior is <= 0 or >= 1)
            throw new InvalidDataException("Training requires both price classes and finite, nonzero area variance.");
        var points = training.Select(row => (X: (row.SquareFeet - mean) / scale,
            Y: row.Price >= priceThreshold ? 1d : 0d)).ToArray();
        var intercept = Math.Log(prior / (1 - prior));
        var slope = 0d;
        var learning = new List<LogisticLearningPoint>();
        for (var epoch = 0; epoch <= epochs; epoch++)
        {
            token.ThrowIfCancellationRequested();
            var model = new LogisticModel(intercept, slope, mean, scale, priceThreshold, prior);
            learning.Add(new(epoch, Evaluate(model, training).LogLoss, Evaluate(model, validation).LogLoss));
            if (epoch == epochs) return new(model, learning.ToArray());
            var interceptGradient = 0d;
            var slopeGradient = 0d;
            foreach (var point in points)
            {
                var error = Sigmoid(intercept + slope * point.X) - point.Y;
                interceptGradient += error;
                slopeGradient += error * point.X;
            }
            intercept -= learningRate * interceptGradient / points.Length;
            slope -= learningRate * slopeGradient / points.Length;
        }
        throw new InvalidOperationException("Logistic training did not produce a model.");
    }

    public static LogisticMetrics Evaluate(LogisticModel model, House[] rows)
    {
        ValidateRows(rows);
        if (!double.IsFinite(model.Intercept) || !double.IsFinite(model.Slope) ||
            !double.IsFinite(model.AreaMean) || !double.IsFinite(model.AreaScale) || model.AreaScale <= 0 ||
            !double.IsFinite(model.PriceThreshold) || model.PriceThreshold is <= 100_000 or >= 3_000_000 ||
            !double.IsFinite(model.TrainingPositiveRate) || model.TrainingPositiveRate is <= 0 or >= 1)
            throw new InvalidDataException("The logistic model must contain finite coefficients and a valid training baseline.");
        var tp = 0;
        var fp = 0;
        var tn = 0;
        var fn = 0;
        var baselineCorrect = 0;
        var loss = 0d;
        foreach (var row in rows)
        {
            var positive = row.Price >= model.PriceThreshold;
            var probability = model.PredictProbability(row.SquareFeet);
            var predicted = probability >= 0.5;
            if (positive && predicted) tp++;
            else if (!positive && predicted) fp++;
            else if (!positive) tn++;
            else fn++;
            if (positive == (model.TrainingPositiveRate >= 0.5)) baselineCorrect++;
            var bounded = Math.Clamp(probability, 1e-15, 1 - 1e-15);
            loss -= positive ? Math.Log(bounded) : Math.Log(1 - bounded);
        }
        return new(rows.Length, (tp + tn) / (double)rows.Length, loss / rows.Length,
            tp + fp == 0 ? null : tp / (double)(tp + fp),
            tp + fn == 0 ? null : tp / (double)(tp + fn),
            tp, fp, tn, fn, baselineCorrect / (double)rows.Length);
    }

    private static void ValidateRows(House[] rows)
    {
        if (rows.Length < 2 || rows.Any(row => !double.IsFinite(row.SquareFeet) || !double.IsFinite(row.Price) ||
            row.SquareFeet is < 500 or > 5000 || row.Price is < 100_000 or > 3_000_000))
            throw new InvalidDataException("Logistic regression requires at least two valid NYC residential sales.");
    }
}
