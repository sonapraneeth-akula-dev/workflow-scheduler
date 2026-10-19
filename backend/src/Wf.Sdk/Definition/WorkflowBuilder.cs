using System.Collections.Immutable;
using Wf.Contracts.Definition;
using Wf.Validation;

namespace Wf.Sdk;

public sealed class WorkflowBuilder
{
    private readonly string name;
    private readonly string version;
    private readonly List<NodeBuilder> nodes = [];
    private readonly List<VariableSchema> variables = [];
    private readonly Dictionary<string, string> metadata = new(StringComparer.Ordinal);
    private string? type;
    private PolicyOverrides defaults = new();
    private TimeSpan runTimeout = TimeSpan.FromHours(24);
    private readonly PolicyBuilder placement = new();

    internal WorkflowBuilder(string name, string version) => (this.name, this.version) = (name, version);

    public WorkflowBuilder RunsOn(params PlacementSelector[] selectors) { placement.RunsOn(selectors); return this; }
    public WorkflowBuilder RunsOn(Func<PlacementVariables, PlacementSelector> configure) { placement.RunsOn(configure); return this; }
    public WorkflowBuilder Affinity(PlacementAffinity value) { placement.Affinity(value); return this; }

    public WorkflowBuilder Defaults(Action<PolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new PolicyBuilder();
        configure(builder);
        defaults = builder.Build();
        return this;
    }

    public WorkflowBuilder RunTimeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(value), "Run timeout must be positive.");
        runTimeout = value;
        return this;
    }

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

    public WorkflowBuilder Node(string key, Action<NodeBuilder> configure, FailurePolicy failure = FailurePolicy.Hard)
    {
        ValidateKey(key);
        if (key.StartsWith('$'))
            throw new ArgumentException($"Node key '{key}' is reserved for lifecycle stages.", nameof(key));
        ArgumentNullException.ThrowIfNull(configure);
        var node = new NodeBuilder(key);
        node.OnFailure(failure);
        configure(node);
        nodes.Add(node);
        return this;
    }

    public WorkflowBuilder SystemInit(Action<TaskContext> execute) => Stage(LifecycleStages.SystemInit, execute);
    public WorkflowBuilder SystemInit(Func<TaskContext, Task> execute) => Stage(LifecycleStages.SystemInit, execute);
    public WorkflowBuilder SystemInit(Func<StageBuilder, StageBuilder> configure) => Stage(LifecycleStages.SystemInit, configure);
    public WorkflowBuilder PreSetup(Action<TaskContext> execute) => Stage(LifecycleStages.PreSetup, execute);
    public WorkflowBuilder PreSetup(Func<TaskContext, Task> execute) => Stage(LifecycleStages.PreSetup, execute);
    public WorkflowBuilder PreSetup(Func<StageBuilder, StageBuilder> configure) => Stage(LifecycleStages.PreSetup, configure);
    public WorkflowBuilder PostProcess(Action<TaskContext> execute) => Stage(LifecycleStages.PostProcess, execute);
    public WorkflowBuilder PostProcess(Func<TaskContext, Task> execute) => Stage(LifecycleStages.PostProcess, execute);
    public WorkflowBuilder PostProcess(Func<StageBuilder, StageBuilder> configure) => Stage(LifecycleStages.PostProcess, configure);
    public WorkflowBuilder SystemPostProcess(Action<TaskContext> execute) => Stage(LifecycleStages.SystemPostProcess, execute);
    public WorkflowBuilder SystemPostProcess(Func<TaskContext, Task> execute) => Stage(LifecycleStages.SystemPostProcess, execute);
    public WorkflowBuilder SystemPostProcess(Func<StageBuilder, StageBuilder> configure) => Stage(LifecycleStages.SystemPostProcess, configure);

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
        var workflowPlacement = PlacementPolicy.Inherit(defaults.Placement, placement.Build().Placement);
        var definitions = nodes.Select(n => n.Build(defaults, workflowPlacement)).ToImmutableArray();
        DagValidator.Validate(definitions);
        PlacementResolver.Validate(definitions, variables);
        ConditionRules.Validate(definitions, variables);
        var tasks = ImmutableDictionary.CreateBuilder<string, Func<TaskContext, Task>>(StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var task in node.Handlers)
                tasks.Add($"{node.Key}/{task.Key}", task.Value);
        return new(name, version, definitions, tasks.ToImmutable(), variables.ToImmutableArray(),
            new(type, metadata.ToImmutableSortedDictionary(StringComparer.Ordinal)), runTimeout, workflowPlacement);
    }

    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('/'))
            throw new ArgumentException($"Key '{key}' cannot contain '/'.", nameof(key));
    }
}
