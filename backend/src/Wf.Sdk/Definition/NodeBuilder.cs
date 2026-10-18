using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class NodeBuilder
{
    private readonly List<DependencyDefinition> dependencies = [];
    private readonly List<(string Key, FailurePolicy Failure, PolicyOverrides Policy, HttpOperationDefinition? HttpOperation, TaskAction Action)> tasks = [];
    private readonly List<InputBinding> inputs = [];
    private readonly List<KeyValuePair<string, Func<TaskContext, Task>>> handlers = [];
    private NodeExecutionMode mode;
    private int? maxParallel;
    private FailurePolicy failure;
    private WhenUnavailable whenUnavailable;
    private readonly PolicyBuilder policy = new();
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

    public NodeBuilder Timeout(TimeSpan value) { policy.Timeout(value); return this; }
    public NodeBuilder Retry(int count, BackoffPolicy? backoff = null, IEnumerable<FailureCategory>? on = null)
    { policy.Retry(count, backoff, on); return this; }
    public NodeBuilder NoAutoRetry() { policy.NoAutoRetry(); return this; }
    public NodeBuilder Limits(double cpuCores = 0, ulong ramMb = 0) { policy.Limits(cpuCores, ramMb); return this; }
    public NodeBuilder RunsOn(params PlacementSelector[] selectors) { policy.RunsOn(selectors); return this; }
    public NodeBuilder RunsOn(Func<PlacementVariables, PlacementSelector> configure) { policy.RunsOn(configure); return this; }
    public NodeBuilder Affinity(PlacementAffinity value) { policy.Affinity(value); return this; }
    public NodeBuilder WhenUnavailable(WhenUnavailable value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        whenUnavailable = value;
        return this;
    }

    public NodeBuilder Task(string key, Func<TaskContext, Task> execute, FailurePolicy failure = FailurePolicy.Hard)
        => Task(key, execute, null, failure);

    public NodeBuilder Task(string key, Func<TaskContext, Task> execute, Action<PolicyBuilder>? configure,
        FailurePolicy failure = FailurePolicy.Hard)
    {
        WorkflowBuilder.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(execute);
        var task = new TaskDefinition(key, failure);
        var builder = new PolicyBuilder();
        configure?.Invoke(builder);
        handlers.Add(new(key, execute));
        tasks.Add((task.Key, task.Failure, builder.Build(), null, Tasks.Describe(execute)));
        return this;
    }

    public NodeBuilder Task(string key, HttpOperationTask operation, FailurePolicy failure = FailurePolicy.Hard)
        => Task(key, operation, null, failure);

    public NodeBuilder Task(string key, HttpOperationTask operation, Action<PolicyBuilder>? configure,
        FailurePolicy failure = FailurePolicy.Hard)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task(key, operation.ExecuteAsync, configure, failure);
        tasks[^1] = tasks[^1] with
        {
            HttpOperation = operation.Definition,
            Action = new(TaskActionKind.HttpOperation, "POST " + operation.Definition.SubmissionUrl)
        };
        return this;
    }

    public NodeBuilder Task(string key, Action<TaskContext> execute, FailurePolicy failure = FailurePolicy.Hard)
        => Task(key, execute, null, failure);

    public NodeBuilder Task(string key, Action<TaskContext> execute, Action<PolicyBuilder>? configure,
        FailurePolicy failure = FailurePolicy.Hard)
    {
        ArgumentNullException.ThrowIfNull(execute);
        Task(key, ctx => { execute(ctx); return System.Threading.Tasks.Task.CompletedTask; }, configure, failure);
        tasks[^1] = tasks[^1] with { Action = Tasks.Describe(execute) };
        return this;
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

    internal NodeDefinition Build(PolicyOverrides defaults, PlacementPolicy? workflowPlacement)
    {
        var overrides = policy.Build();
        var nodePlacement = PlacementPolicy.Inherit(workflowPlacement, overrides.Placement);
        if (LifecycleStages.After.Contains(Key) && overrides.Timeout is null)
            overrides = overrides with { Timeout = TimeSpan.FromMinutes(15) };
        return new(Key, dependencies.ToImmutableArray(), tasks.Select(t =>
            new TaskDefinition(t.Key, t.Failure, PolicyResolver.Resolve(defaults, overrides, t.Policy),
                PlacementPolicy.Inherit(nodePlacement, t.Policy.Placement), t.HttpOperation, t.Action)).ToImmutableArray(),
            mode, maxParallel, failure, inputs.ToImmutableArray(), overrides.Timeout, nodePlacement, whenUnavailable);
    }
}
