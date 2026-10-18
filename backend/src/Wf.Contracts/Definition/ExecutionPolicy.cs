using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Definition;

public enum FailureCategory { TaskError, Timeout, AgentLost, PlatformError, ValidationError, Aborted, ResourceLimit, Placement }
public enum BackoffKind { Fixed, Exponential }

public sealed record TaskResourceLimits
{
    public double CpuCores { get; }
    public ulong RamMb { get; }

    public TaskResourceLimits(double cpuCores = 0, ulong ramMb = 0)
    {
        if (!double.IsFinite(cpuCores) || cpuCores != 0 && (cpuCores < 0.01 || cpuCores > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(cpuCores), "CPU limit must be zero (unlimited) or at least 0.01 cores.");
        if (ramMb > long.MaxValue / (1024 * 1024))
            throw new ArgumentOutOfRangeException(nameof(ramMb), "RAM limit exceeds the supported byte count.");
        CpuCores = cpuCores;
        RamMb = ramMb;
    }
}

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskResourceLimits? Limits { get; }
    public static ExecutionPolicy Default { get; } = new(TimeSpan.FromHours(1), new(), true);

    public ExecutionPolicy(TimeSpan attemptTimeout, RetryPolicy retry, bool autoRetry = true, TaskResourceLimits? limits = null)
    {
        if (attemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout), "Attempt timeout must be positive.");
        ArgumentNullException.ThrowIfNull(retry);
        AttemptTimeout = attemptTimeout;
        Retry = retry;
        AutoRetry = autoRetry;
        Limits = limits;
    }
}
