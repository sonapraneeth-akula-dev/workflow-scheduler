using System.Collections.Immutable;

namespace Wf.Contracts.Definition;

public sealed record WorkflowMetadata
{
    public string Type { get; }
    public ImmutableDictionary<string, string> Values { get; }

    public WorkflowMetadata(string type, ImmutableDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (key, value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
        }
        Type = type;
        Values = values.WithComparers(StringComparer.Ordinal);
    }
}
