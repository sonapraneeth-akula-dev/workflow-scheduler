using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed record PolicyOverrides(TimeSpan? Timeout = null, RetryPolicy? Retry = null, bool? AutoRetry = null);

public sealed class PolicyBuilder
{
    private TimeSpan? timeout;
    private RetryPolicy? retry;
    private bool? autoRetry;

    public PolicyBuilder Timeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be positive.");
        timeout = value;
        return this;
    }

    public PolicyBuilder Retry(int count, BackoffPolicy? backoff = null, IEnumerable<FailureCategory>? on = null)
    {
        retry = new(count, backoff, on?.ToImmutableArray() ?? default);
        return this;
    }

    public PolicyBuilder NoAutoRetry()
    {
        autoRetry = false;
        return this;
    }

    internal PolicyOverrides Build() => new(timeout, retry, autoRetry);
}

public static class Backoff
{
    public static BackoffPolicy Fixed(TimeSpan delay) => new(BackoffKind.Fixed, delay, TimeSpan.FromMinutes(10));
    public static BackoffPolicy Exponential(TimeSpan delay, TimeSpan? max = null) =>
        new(BackoffKind.Exponential, delay, max ?? TimeSpan.FromMinutes(10));
}

public static class PolicyResolver
{
    public static ExecutionPolicy Resolve(PolicyOverrides workflow, PolicyOverrides node, PolicyOverrides task)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(task);
        return new(task.Timeout ?? node.Timeout ?? workflow.Timeout ?? ExecutionPolicy.Default.AttemptTimeout,
            task.Retry ?? node.Retry ?? workflow.Retry ?? ExecutionPolicy.Default.Retry,
            task.AutoRetry ?? node.AutoRetry ?? workflow.AutoRetry ?? true);
    }
}
