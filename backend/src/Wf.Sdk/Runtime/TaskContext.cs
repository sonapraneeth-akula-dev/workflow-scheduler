using System.Collections.Immutable;

namespace Wf.Sdk;

public sealed class TaskContext
{
    private readonly ImmutableDictionary<string, object> variables;
    public CancellationToken CancellationToken { get; }

    public TaskContext(CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object>? variables = null)
    {
        CancellationToken = cancellationToken;
        this.variables = variables?.ToImmutableDictionary(StringComparer.Ordinal) ??
            ImmutableDictionary<string, object>.Empty;
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
