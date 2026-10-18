namespace Wf.Contracts.Definition;

public enum DependencyCondition { OnSuccess, OnFailure, Always }

public sealed record DependencyDefinition
{
    public string Node { get; }
    public DependencyCondition When { get; }

    public DependencyDefinition(string node, DependencyCondition when = DependencyCondition.OnSuccess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        if (!Enum.IsDefined(when))
            throw new ArgumentOutOfRangeException(nameof(when));
        Node = node;
        When = when;
    }
}
