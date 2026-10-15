using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class WorkflowDefinition
{
    public string Name { get; }
    public string Version { get; }
    public ImmutableArray<NodeDefinition> Nodes { get; }
    public ImmutableDictionary<string, Func<TaskContext, Task>> Tasks { get; }
    public ImmutableArray<VariableSchema> Variables { get; }
    public WorkflowMetadata Metadata { get; }
    public string Type => Metadata.Type;
    public TimeSpan RunTimeout { get; }
    public PlacementPolicy? Placement { get; }

    internal WorkflowDefinition(string name, string version, ImmutableArray<NodeDefinition> nodes,
        ImmutableDictionary<string, Func<TaskContext, Task>> tasks, ImmutableArray<VariableSchema> variables,
        WorkflowMetadata metadata, TimeSpan runTimeout, PlacementPolicy? placement = null)
    {
        Name = name;
        Version = version;
        Nodes = nodes;
        Tasks = tasks;
        Variables = variables;
        Metadata = metadata;
        RunTimeout = runTimeout;
        Placement = placement;
    }
}
