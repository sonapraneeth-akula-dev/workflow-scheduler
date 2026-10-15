using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class WorkflowBuilder
{
    private readonly string name;
    private readonly string version;
    private readonly List<NodeBuilder> nodes = [];

    internal WorkflowBuilder(string name, string version) => (this.name, this.version) = (name, version);

    public WorkflowBuilder Node(string key, Action<NodeBuilder> configure)
    {
        ValidateKey(key);
        if (key.StartsWith('$'))
            throw new ArgumentException($"Node key '{key}' is reserved for lifecycle stages.", nameof(key));
        ArgumentNullException.ThrowIfNull(configure);
        var node = new NodeBuilder(key);
        configure(node);
        nodes.Add(node);
        return this;
    }

    public WorkflowBuilder SystemInit(Action<TaskContext> execute) => Stage("$init", execute);
    public WorkflowBuilder SystemInit(Func<TaskContext, Task> execute) => Stage("$init", execute);
    public WorkflowBuilder SystemInit(Func<StageBuilder, StageBuilder> configure) => Stage("$init", configure);
    public WorkflowBuilder PreSetup(Action<TaskContext> execute) => Stage("$pre", execute);
    public WorkflowBuilder PreSetup(Func<TaskContext, Task> execute) => Stage("$pre", execute);
    public WorkflowBuilder PreSetup(Func<StageBuilder, StageBuilder> configure) => Stage("$pre", configure);
    public WorkflowBuilder PostProcess(Action<TaskContext> execute) => Stage("$post", execute);
    public WorkflowBuilder PostProcess(Func<TaskContext, Task> execute) => Stage("$post", execute);
    public WorkflowBuilder PostProcess(Func<StageBuilder, StageBuilder> configure) => Stage("$post", configure);
    public WorkflowBuilder SystemPostProcess(Action<TaskContext> execute) => Stage("$sys-post", execute);
    public WorkflowBuilder SystemPostProcess(Func<TaskContext, Task> execute) => Stage("$sys-post", execute);
    public WorkflowBuilder SystemPostProcess(Func<StageBuilder, StageBuilder> configure) => Stage("$sys-post", configure);

    private WorkflowBuilder Stage(string key, Action<TaskContext> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return Stage(key, s => s.Task("run", execute));
    }

    private WorkflowBuilder Stage(string key, Func<TaskContext, Task> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return Stage(key, s => s.Task("run", execute));
    }

    private WorkflowBuilder Stage(string key, Func<StageBuilder, StageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var node = new NodeBuilder(key);
        configure(new StageBuilder(node));
        nodes.Add(node);
        return this;
    }

    internal WorkflowDefinition Build()
    {
        var definitions = nodes.Select(n => n.Build()).ToImmutableArray();
        DagValidator.Validate(definitions);
        var tasks = ImmutableDictionary.CreateBuilder<string, Func<TaskContext, Task>>(StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var task in node.Handlers)
                tasks.Add($"{node.Key}/{task.Key}", task.Value);
        return new(name, version, definitions, tasks.ToImmutable());
    }

    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('/'))
            throw new ArgumentException($"Key '{key}' cannot contain '/'.", nameof(key));
    }
}
