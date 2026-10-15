using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public static class Os
{
    public static PlacementSelector Windows { get; } = new(os: "windows");
    public static PlacementSelector Linux { get; } = new(os: "linux");
    public static PlacementSelector From(SelectorValue value) => new(os: value ?? throw new ArgumentNullException(nameof(value)));
}

public static class Machines
{
    public static PlacementSelector Named(params SelectorValue[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Length == 0) throw new ArgumentException("Named needs at least one machine or variable.", nameof(names));
        return new(machines: names.ToImmutableArray());
    }

    public static PlacementSelector Pool(SelectorValue name) => new(pool: name ?? throw new ArgumentNullException(nameof(name)));
    public static PlacementSelector Label(string key, SelectorValue? value = null) => new(labels: [new(key, value)]);

    public static PlacementSelector All(params PlacementSelector[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        SelectorValue? os = null;
        SelectorValue? pool = null;
        var labels = ImmutableArray.CreateBuilder<LabelSelector>();
        var machines = ImmutableArray.CreateBuilder<SelectorValue>();
        foreach (var selector in selectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            if (selector.Os is not null && os is not null && selector.Os != os)
                throw new ArgumentException("RunsOn cannot combine conflicting OS selectors.", nameof(selectors));
            if (selector.Pool is not null && pool is not null && selector.Pool != pool)
                throw new ArgumentException("RunsOn cannot combine conflicting pool selectors.", nameof(selectors));
            if (!selector.Machines.IsEmpty && machines.Count != 0)
                throw new ArgumentException("Combine machine alternatives in one Machines.Named call.", nameof(selectors));
            os ??= selector.Os;
            pool ??= selector.Pool;
            labels.AddRange(selector.Labels);
            machines.AddRange(selector.Machines);
        }
        return new(os, pool, labels.ToImmutable(), machines.ToImmutable());
    }
}

// The indexer records a reference, never reads a run's value during description.
public sealed class PlacementVariables
{
    internal PlacementVariables() { }
    public SelectorValue this[string key] => new(variable: key);
}

public static class Affinity
{
    public static PlacementAffinity None { get; } = new(AffinityKind.None);
    public static PlacementAffinity Node { get; } = new(AffinityKind.Node);
    public static PlacementAffinity Run { get; } = new(AffinityKind.Run);
    public static PlacementAffinity SameMachineAs(string node) => new(AffinityKind.SameMachineAs, node);
}
