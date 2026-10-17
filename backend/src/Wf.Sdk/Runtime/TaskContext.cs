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
    private readonly IReadOnlyDictionary<string, string> secrets;
    public CancellationToken CancellationToken { get; }
    public NodeOutputs Outputs { get; }
    public string? TaskKey { get; }
    public Guid RunId { get; }
    public Action<string> Logger { get; }
    public AbortRecord? Abort { get; }
    public CheckpointStore Checkpoint { get; }
    public TaskAttempt Attempt { get; }
    public IReports Reports => reports ?? throw new NotSupportedException("Report publishing is not configured for this task host.");
    public IArtifacts Artifacts => artifacts ?? throw new NotSupportedException("Artifact publishing is not configured for this task host.");

    public TaskContext(CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object>? variables = null, NodeOutputs? outputs = null,
        IReadOnlyDictionary<string, NodeOutputs>? inputs = null, IEnumerable<InputBinding>? declaredInputs = null,
        Action<string>? logger = null, IReports? reports = null, IArtifacts? artifacts = null, string? taskKey = null,
        AbortRecord? abort = null, IReadOnlyDictionary<string, string>? secrets = null,
        CheckpointStore? checkpoint = null, TaskAttempt? attempt = null, Guid runId = default)
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
        RunId = runId;
        Abort = abort;
        Checkpoint = checkpoint ?? new();
        Attempt = attempt ?? new(1);
        this.secrets = secrets?.ToImmutableDictionary(StringComparer.Ordinal) ??
            ImmutableDictionary<string, string>.Empty;
        Checkpoint.AddSecrets(this.secrets.Values);
    }

    internal TaskContext ForTask(string key, CheckpointStore? checkpoint = null, TaskAttempt? attempt = null) =>
        new(CancellationToken, variables, Outputs, inputs,
        declaredInputs, Logger, reports is ReportPublisher publisher ? publisher.ForSource(key) : reports,
        artifacts is ArtifactPublisher artifactPublisher ? artifactPublisher.ForSource(key) : artifacts, key, Abort, secrets,
        checkpoint ?? Checkpoint, attempt ?? Attempt, RunId);

    /// <summary>Read an agent-resolved reference for this attempt; never writes the value into variables or the manifest.</summary>
    public string Secret(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return secrets.TryGetValue(reference.Name, out var value) ? value :
            throw new TaskFailureException($"Secret '{reference.Name}' is not available for this attempt.",
                retryable: false, category: FailureCategory.ValidationError);
    }

    public string Secret(string reference) => SecretReference.IsValidName(reference) ? Secret(new SecretReference(reference)) :
        throw new TaskFailureException("Task secret reference is invalid.", retryable: false, category: FailureCategory.ValidationError);

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
