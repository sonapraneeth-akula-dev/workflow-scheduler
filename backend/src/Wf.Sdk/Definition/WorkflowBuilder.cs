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
        ArgumentNullException.ThrowIfNull(configure);
        var node = new NodeBuilder(key);
        configure(node);
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
