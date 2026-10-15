using System.Collections.Immutable;

namespace Wf.Contracts.Definition;

public enum NodeExecutionMode { Series, Parallel }

public sealed record TaskDefinition
{
    public string Key { get; }
    public FailurePolicy Failure { get; }
    public ExecutionPolicy Policy { get; }

    public TaskDefinition(string key, FailurePolicy failure = FailurePolicy.Hard, ExecutionPolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(failure));
        Key = key;
        Failure = failure;
        Policy = policy ?? ExecutionPolicy.Default;
    }
}

public sealed record NodeDefinition
{
    public string Key { get; }
    public ImmutableArray<DependencyDefinition> Dependencies { get; }
    public ImmutableArray<TaskDefinition> Tasks { get; }
    public NodeExecutionMode Mode { get; }
    public int? MaxParallel { get; }
    public FailurePolicy Failure { get; }
    public ImmutableArray<InputBinding> Inputs { get; }
    public TimeSpan? Timeout { get; }

    public NodeDefinition(string key, ImmutableArray<DependencyDefinition> dependencies, ImmutableArray<TaskDefinition> tasks,
        NodeExecutionMode mode = NodeExecutionMode.Series, int? maxParallel = null,
        FailurePolicy failure = FailurePolicy.Hard, ImmutableArray<InputBinding> inputs = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (dependencies.IsDefault || tasks.IsDefault)
            throw new ArgumentException("Node dependencies and tasks must be initialized.");
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(failure));
        if (maxParallel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxParallel), "Parallelism must be at least 1.");
        if (mode == NodeExecutionMode.Series && maxParallel is not null)
            throw new ArgumentException("Series nodes cannot specify maxParallel.", nameof(maxParallel));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Node timeout must be positive.");
        Key = key;
        Dependencies = dependencies;
        Tasks = tasks;
        Mode = mode;
        MaxParallel = maxParallel;
        Failure = failure;
        Inputs = inputs.IsDefault ? [] : inputs;
        Timeout = timeout;
    }
}
