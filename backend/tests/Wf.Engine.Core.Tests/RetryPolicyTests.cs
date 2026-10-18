using FsCheck;
using FsCheck.Fluent;
using Wf.Contracts.Definition;
using Wf.Contracts;
using Wf.Engine.Core.Retries;

namespace Wf.Engine.Core.Tests;

public sealed class RetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BackoffKind.Fixed, 0, 0, 8)]
    [InlineData(BackoffKind.Fixed, 2, 0.5, 10)]
    [InlineData(BackoffKind.Fixed, 2, 1, 12)]
    [InlineData(BackoffKind.Exponential, 0, 0.5, 10)]
    [InlineData(BackoffKind.Exponential, 2, 0.5, 40)]
    [InlineData(BackoffKind.Exponential, 9, 1, 60)]
    public void BackoffIsFixedOrExponentialWithBoundedJitter(BackoffKind kind, int used, double jitter, double seconds)
    {
        var policy = new ExecutionPolicy(TimeSpan.FromHours(1),
            new(10, new(kind, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1))));
        var result = RetryPolicyEvaluator.Evaluate(policy, Context(author: used), jitter);
        Assert.True(result.Retry);
        Assert.Equal(RetryBudget.Author, result.Budget);
        Assert.Equal(Now.AddSeconds(seconds), result.RetryAt);
    }

    [Theory]
    [InlineData(FailureCategory.ValidationError)]
    [InlineData(FailureCategory.Aborted)]
    [InlineData(FailureCategory.ResourceLimit)]
    [InlineData(FailureCategory.Placement)]
    public void ForbiddenCategoriesAreNeverRetriedEvenWhenExplicitlyListed(FailureCategory category)
    {
        var policy = new ExecutionPolicy(TimeSpan.FromHours(1), new(10, on: [category]));
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context(category), 0.5).Retry);
    }

    [Fact]
    public void CancelledAndNonRetryableFailuresAreNotRetried()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromHours(1), new(10));
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context() with { Retryable = false }, 0.5).Retry);
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context() with { Cancelled = true }, 0.5).Retry);
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context(), 0.5, new NonRetryableException("permanent")).Retry);
    }

    [Fact]
    public void BudgetsAreIndependentAndNoAutoRetryDisablesOnlyPlatformBudget()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromHours(1), new(3, on: [FailureCategory.AgentLost]));
        var platform = RetryPolicyEvaluator.Evaluate(policy, Context(FailureCategory.AgentLost, author: 3), 0.5);
        Assert.True(platform.Retry);
        Assert.Equal(RetryBudget.Platform, platform.Budget);
        Assert.Equal(Now, platform.RetryAt);
        var author = RetryPolicyEvaluator.Evaluate(policy, Context(FailureCategory.AgentLost, platform: 2), 0.5);
        Assert.Equal(RetryBudget.Author, author.Budget);
        var disabled = new ExecutionPolicy(TimeSpan.FromHours(1), policy.Retry, false);
        Assert.Equal(RetryBudget.Author, RetryPolicyEvaluator.Evaluate(disabled, Context(FailureCategory.AgentLost), 0.5).Budget);
        Assert.False(RetryPolicyEvaluator.Evaluate(new(TimeSpan.FromHours(1), new(), false),
            Context(FailureCategory.AgentLost), 0.5).Retry);
    }

    [Fact]
    public void ExhaustedBudgetsOrUnlistedCategoriesStopExplicitly()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromHours(1), new(2));
        var stopped = RetryPolicyEvaluator.Evaluate(policy, Context(author: 2), 0.5);
        Assert.False(stopped.Retry);
        Assert.Null(stopped.RetryAt);
        Assert.Equal(RetryBudget.None, stopped.Budget);
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context(FailureCategory.AgentLost, platform: 2), 0.5).Retry);
    }

    [Fact]
    public void RetryCannotStartAtOrBeyondEarliestDeadline()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromMinutes(1),
            new(1, new(BackoffKind.Fixed, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1))));
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context() with { RunDeadline = Now.AddSeconds(10) }, 0.5).Retry);
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context() with { NodeDeadline = Now.AddSeconds(9) }, 0.5).Retry);
        Assert.True(RetryPolicyEvaluator.Evaluate(policy, Context() with { NodeDeadline = Now.AddSeconds(11) }, 0.5).Retry);
        Assert.False(RetryPolicyEvaluator.Evaluate(policy, Context(FailureCategory.PlatformError) with { RunDeadline = Now }, 0.5).Retry);
    }

    [Fact]
    public void JitterBoundsHoldAcrossGeneratedInputs()
    {
        Check.QuickThrowOnFailure(Prop.ForAll<int, int, int>((rawBase, rawUsed, rawJitter) =>
        {
            var seconds = Math.Abs((long)rawBase) % 600 + 1;
            var used = (int)(Math.Abs((long)rawUsed) % 10);
            var jitter = (uint)rawJitter / (double)uint.MaxValue;
            var policy = new ExecutionPolicy(TimeSpan.FromHours(1),
                new(10, new(BackoffKind.Exponential, TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(10))));
            var result = RetryPolicyEvaluator.Evaluate(policy, Context(author: used), jitter);
            Assert.True(result.Retry);
            var nominal = Math.Min(seconds * Math.Pow(2, used), 600) * TimeSpan.TicksPerSecond;
            var ticks = (result.RetryAt!.Value - Now).Ticks;
            Assert.InRange(ticks, (long)(nominal * 0.8) - 1, (long)Math.Min(nominal * 1.2, 600 * TimeSpan.TicksPerSecond) + 1);
            return true;
        }));
    }

    [Fact]
    public void ZeroDelayAndHugeRetryIndexCannotOverflowBackoff()
    {
        var policy = new ExecutionPolicy(TimeSpan.FromHours(1),
            new(int.MaxValue, new(BackoffKind.Exponential, TimeSpan.Zero, TimeSpan.FromMinutes(10))));
        Assert.Equal(Now, RetryPolicyEvaluator.Evaluate(policy, Context(author: 10_000), 1).RetryAt);
        policy = new(TimeSpan.FromHours(1),
            new(int.MaxValue, new(BackoffKind.Exponential, TimeSpan.MaxValue, TimeSpan.MaxValue)));
        Assert.Equal(Now.AddMinutes(10), RetryPolicyEvaluator.Evaluate(policy, Context(author: 10_000), 1).RetryAt);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidJitterIsRejected(double jitter) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicyEvaluator.Evaluate(ExecutionPolicy.Default, Context(), jitter));

    private static RetryContext Context(FailureCategory category = FailureCategory.TaskError, int author = 0, int platform = 0) =>
        new(category, author, platform, Now, Now.AddHours(24));
}
