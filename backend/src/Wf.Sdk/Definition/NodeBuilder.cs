using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class NodeBuilder
{
    private readonly List<DependencyDefinition> dependencies = [];
    private readonly List<TaskDefinition> tasks = [];
    private readonly List<InputBinding> inputs = [];
    private readonly List<KeyValuePair<string, Func<TaskContext, Task>>> handlers = [];
    private NodeExecutionMode mode;
    private int? maxParallel;
    private FailurePolicy failure;
    internal string Key { get; }
    internal IEnumerable<KeyValuePair<string, Func<TaskContext, Task>>> Handlers => handlers;

    internal NodeBuilder(string key) => Key = key;

    public NodeBuilder DependsOn(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
            WorkflowBuilder.ValidateKey(key);
        dependencies.AddRange(keys.Select(key => new DependencyDefinition(key)));
        return this;
    }

    public NodeBuilder DependsOn(string key, DependencyCondition when)
    {
        WorkflowBuilder.ValidateKey(key);
        dependencies.Add(new(key, when));
        return this;
    }

    public NodeBuilder OnFailure(FailurePolicy policy)
    {
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));
        failure = policy;
        return this;
    }

    public NodeBuilder Input(string node, string key)
    {
        WorkflowBuilder.ValidateKey(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        inputs.Add(new(node, key));
        return this;
    }

    public NodeBuilder Task(string key, Func<TaskContext, Task> execute, FailurePolicy failure = FailurePolicy.Hard)
    {
        WorkflowBuilder.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(execute);
        var task = new TaskDefinition(key, failure);
        handlers.Add(new(key, execute));
        tasks.Add(task);
        return this;
    }

    public NodeBuilder Task(string key, Action<TaskContext> execute, FailurePolicy failure = FailurePolicy.Hard)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return Task(key, ctx => { execute(ctx); return System.Threading.Tasks.Task.CompletedTask; }, failure);
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
        mode, maxParallel, failure, inputs.ToImmutableArray());
}
