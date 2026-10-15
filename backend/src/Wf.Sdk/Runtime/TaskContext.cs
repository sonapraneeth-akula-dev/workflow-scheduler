using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class TaskContext
{
    private readonly ImmutableDictionary<string, object> variables;
    private readonly ImmutableDictionary<string, NodeOutputs> inputs;
    private readonly ImmutableHashSet<InputBinding> declaredInputs;
    private readonly IReports? reports;
    private readonly IArtifacts? artifacts;
    public CancellationToken CancellationToken { get; }
    public NodeOutputs Outputs { get; }
    public string? TaskKey { get; }
    public Action<string> Logger { get; }
    public AbortRecord? Abort { get; }
    public IReports Reports => reports ?? throw new NotSupportedException("Report publishing is not configured for this task host.");
    public IArtifacts Artifacts => artifacts ?? throw new NotSupportedException("Artifact publishing is not configured for this task host.");

    public TaskContext(CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object>? variables = null, NodeOutputs? outputs = null,
        IReadOnlyDictionary<string, NodeOutputs>? inputs = null, IEnumerable<InputBinding>? declaredInputs = null,
        Action<string>? logger = null, IReports? reports = null, IArtifacts? artifacts = null, string? taskKey = null,
        AbortRecord? abort = null)
    {
        CancellationToken = cancellationToken;
        Outputs = outputs ?? new("task");
        this.variables = variables?.ToImmutableDictionary(StringComparer.Ordinal) ??
            ImmutableDictionary<string, object>.Empty;
        this.inputs = inputs?.ToImmutableDictionary(StringComparer.Ordinal) ??
            ImmutableDictionary<string, NodeOutputs>.Empty;
        this.declaredInputs = declaredInputs?.ToImmutableHashSet() ?? [];
        Logger = logger ?? Console.WriteLine;
        this.reports = reports;
        this.artifacts = artifacts;
        TaskKey = taskKey;
        Abort = abort;
    }

    internal TaskContext ForTask(string key) => new(CancellationToken, variables, Outputs, inputs,
        declaredInputs, Logger, reports, artifacts, key, Abort);

    public void Log(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Logger(message);
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
        throw new InvalidOperationException($"Variable '{key}' cannot be read as {typeof(T).Name}.");
    }

    public T VarEnum<T>(string key) where T : struct, Enum
    {
        var value = Var(key);
        if (value is T typed)
            return typed;
        if (value is string text && Enum.TryParse<T>(text, false, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidOperationException($"Variable '{key}' is not a defined {typeof(T).Name} value.");
    }
}
