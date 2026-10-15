using System.Collections.Immutable;

namespace Wf.Contracts.Definition;

public enum NodeExecutionMode { Series, Parallel }

public sealed record TaskDefinition(string Key);

public sealed record NodeDefinition
{
    public string Key { get; }
    public ImmutableArray<string> Dependencies { get; }
    public ImmutableArray<TaskDefinition> Tasks { get; }
    public NodeExecutionMode Mode { get; }
    public int? MaxParallel { get; }

    public NodeDefinition(string key, ImmutableArray<string> dependencies, ImmutableArray<TaskDefinition> tasks,
        NodeExecutionMode mode = NodeExecutionMode.Series, int? maxParallel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (dependencies.IsDefault || tasks.IsDefault)
            throw new ArgumentException("Node dependencies and tasks must be initialized.");
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (maxParallel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxParallel), "Parallelism must be at least 1.");
        if (mode == NodeExecutionMode.Series && maxParallel is not null)
            throw new ArgumentException("Series nodes cannot specify maxParallel.", nameof(maxParallel));
        Key = key;
        Dependencies = dependencies;
        Tasks = tasks;
        Mode = mode;
        MaxParallel = maxParallel;
    }
}
