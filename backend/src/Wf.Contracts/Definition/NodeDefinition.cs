using System.Collections.Immutable;

namespace Wf.Contracts.Definition;

public enum NodeExecutionMode { Series, Parallel }

public sealed record TaskDefinition(string Key);

public sealed record NodeDefinition(
    string Key,
    ImmutableArray<string> Dependencies,
    ImmutableArray<TaskDefinition> Tasks,
    NodeExecutionMode Mode = NodeExecutionMode.Series,
    int? MaxParallel = null);
