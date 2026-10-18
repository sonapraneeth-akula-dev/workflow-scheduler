using Wf.Contracts.Definition;
using Wf.Contracts;

namespace Wf.Engine.Core.Retries;

public enum RetryBudget { None, Author, Platform }
public sealed record RetryContext(FailureCategory Category, int AuthorRetriesUsed, int PlatformRetriesUsed,
    DateTimeOffset Now, DateTimeOffset RunDeadline, DateTimeOffset? NodeDeadline = null,
    bool Retryable = true, bool Cancelled = false);
public sealed record RetryDecision(bool Retry, DateTimeOffset? RetryAt, RetryBudget Budget, string Reason);

public static class RetryPolicyEvaluator
{
    public const int PlatformRetryBudget = 2;

    public static RetryDecision Evaluate(ExecutionPolicy policy, RetryContext context, double jitterUnit, Exception? error = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(context.Category) || context.AuthorRetriesUsed < 0 || context.PlatformRetriesUsed < 0)
            throw new ArgumentException("Retry context has an invalid category or retry count.", nameof(context));
        if (!double.IsFinite(jitterUnit) || jitterUnit is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(jitterUnit));
        if (!context.Retryable || context.Cancelled || error is TaskFailureException { Retryable: false } ||
            context.Category is FailureCategory.ValidationError or
            FailureCategory.Aborted or FailureCategory.ResourceLimit or FailureCategory.Placement)
            return Stop("The failure is not retryable.");

        var platform = context.Category is FailureCategory.AgentLost or FailureCategory.PlatformError;
        RetryBudget budget;
        TimeSpan delay;
        if (platform && policy.AutoRetry && context.PlatformRetriesUsed < PlatformRetryBudget)
        {
            budget = RetryBudget.Platform;
            delay = TimeSpan.Zero;
        }
        else
        {
            if (context.AuthorRetriesUsed >= policy.Retry.Retries || !policy.Retry.On.Contains(context.Category))
                return Stop("No author retries remain for this failure category.");
            budget = RetryBudget.Author;
            var backoff = policy.Retry.Backoff;
            var maxTicks = Math.Min(backoff.MaxDelay.Ticks, TimeSpan.FromMinutes(10).Ticks);
            var nominal = backoff.Kind == BackoffKind.Fixed ? backoff.Delay.Ticks :
                Math.ScaleB(backoff.Delay.Ticks, Math.Min(context.AuthorRetriesUsed, 63));
            nominal = Math.Min(nominal, maxTicks);
            delay = TimeSpan.FromTicks((long)Math.Min(maxTicks, Math.Round(nominal * (0.8 + 0.4 * jitterUnit))));
        }
        var deadline = context.NodeDeadline is { } nodeDeadline && nodeDeadline < context.RunDeadline
            ? nodeDeadline : context.RunDeadline;
        if (deadline - context.Now <= delay)
            return Stop("The next attempt cannot start before its deadline.");
        return new(true, context.Now + delay, budget, "Retry scheduled.");
    }

    private static RetryDecision Stop(string reason) => new(false, null, RetryBudget.None, reason);
}
