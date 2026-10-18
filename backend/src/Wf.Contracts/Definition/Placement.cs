using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Definition;

public sealed record SelectorValue
{
    public string? Literal { get; }
    public string? Variable { get; }

    public SelectorValue(string? literal = null, string? variable = null)
    {
        if ((literal is null) == (variable is null) || string.IsNullOrWhiteSpace(literal ?? variable))
            throw new ArgumentException("A selector value needs exactly one non-empty literal or variable name.");
        Literal = literal;
        Variable = variable;
    }

    public static implicit operator SelectorValue(string literal) => new(literal: literal);
}

public sealed record LabelSelector
{
    public string Key { get; }
    public SelectorValue? Value { get; }

    public LabelSelector(string key, SelectorValue? value = null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Contains('='))
            throw new ArgumentException("A label key must be 1-100 characters and cannot contain '='.", nameof(key));
        if (value?.Literal is { Length: > 200 }) throw new ArgumentException("Label values must fit 200 characters.", nameof(value));
        Key = key;
        Value = value;
    }
}

public sealed record PlacementSelector
{
    public SelectorValue? Os { get; }
    public SelectorValue? Pool { get; }
    public ImmutableArray<LabelSelector> Labels { get; }
    public ImmutableArray<SelectorValue> Machines { get; }
    public ImmutableArray<Guid> MachineIds { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; }

    public PlacementSelector(SelectorValue? os = null, SelectorValue? pool = null,
        ImmutableArray<LabelSelector> labels = default, ImmutableArray<SelectorValue> machines = default,
        ImmutableArray<Guid> machineIds = default, int? count = null)
    {
        if (os?.Literal is { } osName && osName is not ("windows" or "linux"))
            throw new ArgumentException("Selector OS must be windows or linux.", nameof(os));
        if (pool?.Literal is { Length: > 200 }) throw new ArgumentException("Pool names must fit 200 characters.", nameof(pool));
        var normalizedLabels = labels.IsDefault ? ImmutableArray<LabelSelector>.Empty : labels;
        var normalizedMachines = machines.IsDefault ? ImmutableArray<SelectorValue>.Empty : machines;
        var normalizedIds = machineIds.IsDefault ? ImmutableArray<Guid>.Empty : machineIds.Distinct().ToImmutableArray();
        if (normalizedLabels.Any(label => label is null) ||
            normalizedLabels.Select(label => label.Key).Distinct(StringComparer.Ordinal).Count() != normalizedLabels.Length)
            throw new ArgumentException("Selector label keys must be non-null and unique.", nameof(labels));
        if (normalizedMachines.Any(machine => machine is null || machine.Literal is { Length: > 200 }))
            throw new ArgumentException("Machine names must be non-null and fit 200 characters.", nameof(machines));
        if (normalizedIds.Contains(Guid.Empty))
            throw new ArgumentException("Machine IDs must be non-empty UUIDs.", nameof(machineIds));
        if (!normalizedIds.IsEmpty && !normalizedMachines.IsEmpty)
            throw new ArgumentException("Select machines by IDs or names, not both.", nameof(machineIds));
        if (count is <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Machine count must be positive.");
        if (!normalizedIds.IsEmpty && count > normalizedIds.Length)
            throw new ArgumentException("Machine count cannot exceed the number of distinct machine IDs.", nameof(count));
        Os = os;
        Pool = pool;
        Labels = normalizedLabels.OrderBy(label => label.Key, StringComparer.Ordinal).ToImmutableArray();
        Machines = normalizedMachines.Distinct().ToImmutableArray();
        MachineIds = normalizedIds;
        Count = count;
    }
}

public enum AffinityKind { None, Node, Run, SameMachineAs }

public sealed record PlacementAffinity
{
    public AffinityKind Kind { get; }
    public string? NodeKey { get; }

    public PlacementAffinity(AffinityKind kind, string? nodeKey = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == AffinityKind.SameMachineAs)
        {
            if (string.IsNullOrWhiteSpace(nodeKey) || nodeKey.Contains('/'))
                throw new ArgumentException("SameMachineAs requires a node key.", nameof(nodeKey));
        }
        else if (nodeKey is not null) throw new ArgumentException("Only SameMachineAs takes a node key.", nameof(nodeKey));
        Kind = kind;
        NodeKey = nodeKey;
    }
}

public sealed record PlacementPolicy(PlacementSelector? Selector = null, PlacementAffinity? Affinity = null)
{
    public static PlacementPolicy? Inherit(PlacementPolicy? outer, PlacementPolicy? inner) =>
        outer is null && inner is null ? null : new(inner?.Selector ?? outer?.Selector, inner?.Affinity ?? outer?.Affinity);
}
