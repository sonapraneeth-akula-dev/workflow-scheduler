using System.Security.Cryptography;

namespace RegressionSample;

public sealed record RegressionScenario(string Mode, int Seed, string FaultStage, int TimeoutPercent,
    int InfraFailurePercent, int ServiceFailurePercent)
{
    public const int TaskTimeoutSeconds = 15;
    public const int RunTimeoutSeconds = 120;

    public static RegressionScenario Choose(string mode, int requestedSeed, string faultStage = "random",
        int timeoutPercent = 10, int infraFailurePercent = 10, int serviceFailurePercent = 10,
        bool runFinalTest = true)
    {
        if (mode is not ("random" or "success" or "task-timeout" or "run-timeout" or "infra-failure" or "service-failure") ||
            faultStage is not ("random" or "train" or "validate" or "test") || requestedSeed < 0 ||
            timeoutPercent is < 0 or > 100 || infraFailurePercent is < 0 or > 100 ||
            serviceFailurePercent is < 0 or > 100 || timeoutPercent + infraFailurePercent + serviceFailurePercent > 100)
            throw new InvalidDataException("Use a known scenario/stage, a nonnegative seed, and failure percentages between 0 and 100 totaling at most 100.");
        var seed = requestedSeed == 0 ? RandomNumberGenerator.GetInt32(1, int.MaxValue) : requestedSeed;
        var random = new Random(seed);
        if (mode == "random")
        {
            var draw = random.Next(100);
            mode = draw < timeoutPercent ? random.Next(2) == 0 ? "task-timeout" : "run-timeout"
                : draw < timeoutPercent + infraFailurePercent ? "infra-failure"
                : draw < timeoutPercent + infraFailurePercent + serviceFailurePercent ? "service-failure"
                : "success";
        }
        var stage = mode switch
        {
            "success" => "none",
            "run-timeout" => "run-budget",
            _ => faultStage == "random"
                ? new[] { "train", "validate", "test" }[random.Next(runFinalTest ? 3 : 2)]
                : faultStage
        };
        if (!runFinalTest && stage == "test")
            throw new InvalidDataException("A test-stage failure requires runFinalTest=true.");
        return new(mode, seed, stage, timeoutPercent, infraFailurePercent, serviceFailurePercent);
    }
}
