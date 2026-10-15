using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class TaskContext
{
    private readonly ImmutableDictionary<string, object> variables;
    private readonly ImmutableDictionary<string, NodeOutputs> inputs;
    private readonly ImmutableHashSet<InputBinding> declaredInputs;
    public CancellationToken CancellationToken { get; }
    public NodeOutputs Outputs { get; }

    public TaskContext(CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object>? variables = null, NodeOutputs? outputs = null,
        IReadOnlyDictionary<string, NodeOutputs>? inputs = null, IEnumerable<InputBinding>? declaredInputs = null)
    {
        CancellationToken = cancellationToken;
        Outputs = outputs ?? new("task");
        this.variables = variables?.ToImmutableDictionary(StringComparer.Ordinal) ??
            ImmutableDictionary<string, object>.Empty;
        this.inputs = inputs?.ToImmutableDictionary(StringComparer.Ordinal) ??
            ImmutableDictionary<string, NodeOutputs>.Empty;
        this.declaredInputs = declaredInputs?.ToImmutableHashSet() ?? [];
    }

    public T? Input<T>(string node, string key, JsonTypeInfo<T>? typeInfo = null)
    {
        var binding = new InputBinding(node, key);
        if (!declaredInputs.Contains(binding))
            throw new InvalidOperationException($"Input '{node}/{key}' was not declared for this node.");
        if (!inputs.TryGetValue(node, out var source))
            throw new KeyNotFoundException($"Input source node '{node}' has no available outputs.");
        return source.Get(key, typeInfo);
    }

    public object Var(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return variables.TryGetValue(key, out var value) ? value :
            throw new KeyNotFoundException($"Variable '{key}' is not bound.");
    }

    public T Var<T>(string key)
    {
        var value = Var(key);
        if (value is T typed)
            return typed;
        if (typeof(T).IsEnum && value is string text && Enum.TryParse(typeof(T), text, false, out var parsed))
            return (T)parsed;
        throw new InvalidOperationException($"Variable '{key}' cannot be read as {typeof(T).Name}.");
    }
}
