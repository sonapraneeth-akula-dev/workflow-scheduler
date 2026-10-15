using System.Collections.Immutable;

namespace Wf.Contracts.Definition;

public enum FailureCategory { TaskError, Timeout, AgentLost, PlatformError, ValidationError, Aborted, ResourceLimit, Placement }
public enum BackoffKind { Fixed, Exponential }

public sealed record BackoffPolicy
{
    public BackoffKind Kind { get; }
    public TimeSpan Delay { get; }
    public TimeSpan MaxDelay { get; }

    public BackoffPolicy(BackoffKind kind, TimeSpan delay, TimeSpan maxDelay)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (delay < TimeSpan.Zero || maxDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), "Backoff delays cannot be negative.");
        Kind = kind;
        Delay = delay;
        MaxDelay = maxDelay;
    }
}

public sealed record RetryPolicy
{
    public int Retries { get; }
    public BackoffPolicy Backoff { get; }
    public ImmutableArray<FailureCategory> On { get; }

    public RetryPolicy(int retries = 0, BackoffPolicy? backoff = null, ImmutableArray<FailureCategory> on = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        Retries = retries;
        Backoff = backoff ?? new(BackoffKind.Fixed, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
        On = on.IsDefault ? [FailureCategory.TaskError, FailureCategory.Timeout] : on;
        if (On.Any(category => !Enum.IsDefined(category)) || On.Distinct().Count() != On.Length)
            throw new ArgumentException("Retry categories must be valid and unique.", nameof(on));
    }
}

public sealed record ExecutionPolicy
{
    public TimeSpan AttemptTimeout { get; }
    public RetryPolicy Retry { get; }
    public bool AutoRetry { get; }
    public static ExecutionPolicy Default { get; } = new(TimeSpan.FromHours(1), new(), true);

    public ExecutionPolicy(TimeSpan attemptTimeout, RetryPolicy retry, bool autoRetry = true)
    {
        if (attemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout), "Attempt timeout must be positive.");
        ArgumentNullException.ThrowIfNull(retry);
        AttemptTimeout = attemptTimeout;
        Retry = retry;
        AutoRetry = autoRetry;
    }
}
