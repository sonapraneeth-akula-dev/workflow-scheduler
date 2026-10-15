using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class NodeBuilder
{
    private readonly List<string> dependencies = [];
    private readonly List<TaskDefinition> tasks = [];
    private readonly List<KeyValuePair<string, Func<TaskContext, Task>>> handlers = [];
    private NodeExecutionMode mode;
    private int? maxParallel;
    internal string Key { get; }
    internal IEnumerable<KeyValuePair<string, Func<TaskContext, Task>>> Handlers => handlers;

    internal NodeBuilder(string key) => Key = key;

    public NodeBuilder DependsOn(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
            WorkflowBuilder.ValidateKey(key);
        dependencies.AddRange(keys);
        return this;
    }

    public NodeBuilder Task(string key, Func<TaskContext, Task> execute)
    {
        WorkflowBuilder.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(execute);
        handlers.Add(new(key, execute));
        tasks.Add(new(key));
        return this;
    }

    public NodeBuilder Task(string key, Action<TaskContext> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return Task(key, ctx => { execute(ctx); return System.Threading.Tasks.Task.CompletedTask; });
    }

    public NodeBuilder Series()
    {
        mode = NodeExecutionMode.Series;
        maxParallel = null;
        return this;
    }

    public NodeBuilder Parallel(int? maxParallel = null)
    {
        if (maxParallel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxParallel), "Parallelism must be at least 1.");
        mode = NodeExecutionMode.Parallel;
        this.maxParallel = maxParallel;
        return this;
    }

    internal NodeDefinition Build() => new(Key, dependencies.ToImmutableArray(), tasks.ToImmutableArray(),
        mode, maxParallel);
}
