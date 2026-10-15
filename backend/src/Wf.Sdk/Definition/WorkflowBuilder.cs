using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class WorkflowBuilder
{
    private readonly string name;
    private readonly string version;
    private readonly List<NodeBuilder> nodes = [];
    private readonly List<VariableSchema> variables = [];
    private readonly Dictionary<string, string> metadata = new(StringComparer.Ordinal);
    private string? type;

    internal WorkflowBuilder(string name, string version) => (this.name, this.version) = (name, version);

    public WorkflowBuilder Type(string value)
    {
        ValidateKey(value);
        if (type is not null)
            throw new ArgumentException("A workflow must declare exactly one type.", nameof(value));
        type = value;
        return this;
    }

    public WorkflowBuilder Metadata(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!metadata.TryAdd(key, value))
            throw new ArgumentException($"Duplicate metadata key '{key}'.", nameof(key));
        return this;
    }

    public WorkflowBuilder Variables(Action<VariablesBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new VariablesBuilder();
        configure(builder);
        foreach (var variable in builder.Build())
        {
            if (variables.Any(v => v.Key == variable.Key))
                throw new ArgumentException($"Duplicate variable key '{variable.Key}'.");
            variables.Add(variable);
        }
        return this;
    }

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
        if (type is null)
            throw new ArgumentException($"Workflow '{name}' must declare exactly one type.");
        var definitions = nodes.Select(n => n.Build()).ToImmutableArray();
        DagValidator.Validate(definitions);
        var tasks = ImmutableDictionary.CreateBuilder<string, Func<TaskContext, Task>>(StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var task in node.Handlers)
                tasks.Add($"{node.Key}/{task.Key}", task.Value);
        return new(name, version, definitions, tasks.ToImmutable(), variables.ToImmutableArray(),
            new(type, metadata.ToImmutableDictionary(StringComparer.Ordinal)));
    }

    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('/'))
            throw new ArgumentException($"Key '{key}' cannot contain '/'.", nameof(key));
    }
}
