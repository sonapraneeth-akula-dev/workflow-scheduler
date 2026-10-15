using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class WorkflowDefinition
{
    public string Name { get; }
    public string Version { get; }
    public ImmutableArray<NodeDefinition> Nodes { get; }
    public ImmutableDictionary<string, Func<TaskContext, Task>> Tasks { get; }

    internal WorkflowDefinition(string name, string version, ImmutableArray<NodeDefinition> nodes,
        ImmutableDictionary<string, Func<TaskContext, Task>> tasks)
    {
        Name = name;
        Version = version;
        Nodes = nodes;
        Tasks = tasks;
    }
}
